using System.Collections.ObjectModel;
using Dianbo.App.Services;
using Dianbo.App.ViewModels;
using Dianbo.Core.Models;
using Dianbo.Core.Playback;
using Dianbo.Core.Services;
using Dianbo.Infrastructure.Playback;
using Dianbo.Infrastructure.Storage;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace Dianbo.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<Playlist> _userPlaylists = [];
    private ObservableCollection<SongViewModel> _detailPlaylistSongs = [];
    private string? _playlistDetailSourcePage;
    private Playlist? _selectedPlaylist;

    private CancellationTokenSource? _playlistLoadCts;
    private Task<IReadOnlyList<Song>?>? _playlistLoadTask;
    private long? _loadingPlaylistId;
    private long _playlistLoadVersion;

    private void CancelPlaylistDetailLoad()
    {
        _playlistLoadVersion++;
        _playlistLoadCts?.Cancel();
        _playlistLoadCts = null;
        _playlistLoadTask = null;
        _loadingPlaylistId = null;
        if (LibLoadingRing is not null) LibLoadingRing.Visibility = Visibility.Collapsed;
    }

    private async void PlaylistItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string tag } && tag.StartsWith("library:playlist:"))
        {
            var idStr = tag["library:playlist:".Length..];
            if (long.TryParse(idStr, out var id))
            {
                var pl = _userPlaylists.FirstOrDefault(p => p.Id == id);
                if (pl is not null)
                {
                    await OpenAndPlayPlaylistAsync(pl);
                }
                else
                {
                    await OpenPlaylistByIdAsync(id, autoPlay: true);
                }
            }
        }
    }

    private async void UserPlaylistsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (UserPlaylistsList.SelectedItem is Playlist pl)
        {
            await OpenAndPlayPlaylistAsync(pl);
        }
    }

    private NavigationViewItem? FindSidebarPlaylistItem(string tag)
    {
        foreach (var item in LibraryNav.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag is string itemTag && itemTag == tag)
            {
                return navItem;
            }
        }
        return null;
    }

    private void UpdateSidebarPlaylistItems()
    {
        var toRemove = LibraryNav.MenuItems
            .OfType<FrameworkElement>()
            .Where(item => !ReferenceEquals(item, NavFond) && !ReferenceEquals(item, NavRecent))
            .ToList();

        foreach (var item in toRemove)
        {
            LibraryNav.MenuItems.Remove(item);
        }

        if (_userPlaylists.Count > 0)
        {
            foreach (var pl in _userPlaylists)
            {
                var item = new NavigationViewItem
                {
                    Content = pl.Name,
                    Tag = $"library:playlist:{pl.Id}",
                    SelectsOnInvoked = true,
                    Icon = new FontIcon { Glyph = "\uE142" }
                };
                ToolTipService.SetToolTip(item, $"{pl.Name}（{pl.MusicCount} 首歌曲）");
                item.DoubleTapped += PlaylistItem_DoubleTapped;
                LibraryNav.MenuItems.Add(item);
            }
        }
    }

    private void ResetSidebarLibraryItems()
    {
        var toRemove = LibraryNav.MenuItems
            .OfType<FrameworkElement>()
            .Where(item => !ReferenceEquals(item, NavFond) && !ReferenceEquals(item, NavRecent))
            .ToList();

        foreach (var item in toRemove)
        {
            LibraryNav.MenuItems.Remove(item);
        }
    }

    private Task PopulateSidebarPlaylistsAsync(bool showNotice = false)
        => RefreshAllPlaylistsAsync(showNotice);

    private async Task RefreshAllPlaylistsAsync(bool showNotice = false)
    {
        if (!_services.Auth.IsSignedIn)
        {
            if (showNotice) ShowLibResultPill("请先登录波点音乐账号以同步云端歌单", LibResultType.Info);
            try
            {
                var localPlaylists = await _services.Playlists.GetPlaylistsAsync(_lifetime.Token);
                _userPlaylists.Clear();
                foreach (var pl in localPlaylists)
                {
                    _userPlaylists.Add(pl);
                }
                PlaylistsHeaderSummary.Text = $"自建歌单（共 {localPlaylists.Count} 个）";
                UpdateSidebarPlaylistItems();
                UpdateLibRootUi();
            }
            catch (Exception ex)
            {
                _services.Log.Write($"load local playlists error: {ex.Message}");
            }
            return;
        }

        try
        {
            if (_currentPage == "library")
            {
                DismissLibResultPillInstant();
                LibLoadingRing.Visibility = Visibility.Visible;
                LibLoadingText.Text = "正在刷新歌单状态...";
            }

            Playlist? fond = null;
            try
            {
                fond = await _services.Api.GetFondPlaylistAsync(_lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _services.Log.Write($"refresh fond playlist error: {ex.Message}");
            }

            IReadOnlyList<Playlist> playlists = [];
            try
            {
                playlists = await _services.Playlists.GetPlaylistsAsync(_lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _services.Log.Write($"refresh user playlists error: {ex.Message}");
            }

            IReadOnlyList<Song> recents = [];
            try
            {
                recents = await _services.History.GetRecentSongsAsync(200, _lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _services.Log.Write($"refresh recent songs error: {ex.Message}");
            }

            if (fond is not null)
            {
                _currentFondPlaylist = fond;
                FondTitle.Text = fond.Name;
                FondCount.Text = $"{fond.MusicCount} 首歌曲";
                LibRootFondSubtitle.Text = $"{fond.MusicCount} 首歌曲";
                if (!string.IsNullOrWhiteSpace(fond.CoverUrl))
                {
                    FondCover.Source = new BitmapImage(new Uri(fond.CoverUrl));
                }
            }

            _userPlaylists.Clear();
            foreach (var pl in playlists)
            {
                _userPlaylists.Add(pl);
            }
            PlaylistsHeaderSummary.Text = $"自建歌单（共 {playlists.Count} 个）";

            _recentSongs.Clear();
            foreach (var song in recents)
            {
                _recentSongs.Add(new SongViewModel(song, _services.Favorites.IsFavorite(song.Id)));
            }
            RecentCount.Text = $"{recents.Count} 首历史歌曲";
            LibRootRecentSubtitle.Text = $"{recents.Count} 首历史歌曲";

            UpdateSidebarPlaylistItems();

            // 如果当前在歌单详情页，联动更新其曲目列表
            if (_selectedPlaylist is { } selected &&
                _currentPage == "library" && _currentLibraryPage == $"library:playlist:{selected.Id}")
            {
                var updated = _userPlaylists.FirstOrDefault(p => p.Id == selected.Id) ?? selected;
                await OpenPlaylistDetailAsync(updated);
            }
            else if (_currentLibraryPage == "library:fond")
            {
                if (fond is not null)
                {
                    try { await _services.Favorites.SyncAsync(_lifetime.Token); } catch { }
                }
                var favoriteSongs = await _services.Favorites.GetFavoriteSongsAsync();
                _fondSongs.Clear();
                foreach (var song in favoriteSongs)
                {
                    _fondSongs.Add(new SongViewModel(song, isFavorite: true));
                }
                FondCount.Text = $"{favoriteSongs.Count} 首歌曲";
                LibRootFondSubtitle.Text = $"{favoriteSongs.Count} 首歌曲";
            }

            if (showNotice)
            {
                var modeText = fond is null ? "（离线模式）" : string.Empty;
                ShowLibResultPill($"已更新歌单状态（共 {playlists.Count} 个自建歌单{modeText}）", fond is null ? LibResultType.Info : LibResultType.Success);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Log.Write($"refresh playlists error: {ex.Message}");
            if (showNotice)
            {
                ShowLibResultPill("刷新歌单失败，请检查网络", LibResultType.Error);
            }
        }
        finally
        {
            LibLoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadUserPlaylistsAsync()
    {
        try
        {
            LibLoadingRing.Visibility = Visibility.Visible;
            LibLoadingText.Text = "正在获取自建歌单...";

            var playlists = await _services.Playlists.GetPlaylistsAsync(_lifetime.Token);
            _userPlaylists.Clear();
            foreach (var pl in playlists)
            {
                _userPlaylists.Add(pl);
            }
            PlaylistsHeaderSummary.Text = $"自建歌单（共 {playlists.Count} 个）";
            UpdateSidebarPlaylistItems();
        }
        catch (Exception ex)
        {
            _services.Log.Write($"load user playlists error: {ex.Message}");
        }
        finally
        {
            LibLoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task OpenPlaylistByIdAsync(long id, bool autoPlay = false)
    {
        var pl = _userPlaylists.FirstOrDefault(p => p.Id == id)
              ?? _discoverPlaylists.FirstOrDefault(p => p.Id == id)
              ?? (_selectedPlaylist?.Id == id ? _selectedPlaylist : null);
        if (pl is null)
        {
            await LoadUserPlaylistsAsync();
            pl = _userPlaylists.FirstOrDefault(p => p.Id == id)
              ?? _discoverPlaylists.FirstOrDefault(p => p.Id == id)
              ?? (_selectedPlaylist?.Id == id ? _selectedPlaylist : null);
        }
        if (pl is not null && _currentPage == "library" && _currentLibraryPage == $"library:playlist:{id}")
        {
            if (autoPlay)
            {
                await OpenAndPlayPlaylistAsync(pl);
            }
            else
            {
                await OpenPlaylistDetailAsync(pl);
            }
        }
    }

    private async Task OpenAndPlayPlaylistAsync(Playlist pl)
    {
        Navigate($"library:playlist:{pl.Id}");
        var request = OpenPlaylistDetailAsync(pl);
        var songs = await request;
        if (songs is { Count: > 0 } && ReferenceEquals(request, _playlistLoadTask) &&
            _currentPage == "library" && _currentLibraryPage == $"library:playlist:{pl.Id}")
        {
            await PlaySongListAsync(songs, 0, navigateToPlaying: false);
        }
    }

    private Task<IReadOnlyList<Song>?> OpenPlaylistDetailAsync(Playlist playlist)
    {
        if (_loadingPlaylistId == playlist.Id && _playlistLoadTask is { IsCompleted: false })
            return _playlistLoadTask;
        CancelPlaylistDetailLoad();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _playlistLoadCts = cts;
        _loadingPlaylistId = playlist.Id;
        return _playlistLoadTask = LoadPlaylistDetailAsync(playlist, cts, _playlistLoadVersion);
    }

    private async Task<IReadOnlyList<Song>?> LoadPlaylistDetailAsync(Playlist playlist, CancellationTokenSource cts, long version)
    {
        var session = _services.Auth.Current;
        bool IsCurrent() => !cts.IsCancellationRequested && version == _playlistLoadVersion &&
            ReferenceEquals(session, _services.Auth.Current) && _currentPage == "library" &&
            _currentLibraryPage == $"library:playlist:{playlist.Id}";
        _detailPlaylistSongs = [];
        DetailPlaylistSongsList.ItemsSource = _detailPlaylistSongs;
        _selectedPlaylist = playlist;
        PlaylistSubHeaderControl.Tag = playlist.Name;

        DetailPlaylistTitle.Text = playlist.Name;
        DetailPlaylistSubtitle.Text = playlist.DisplaySubtitle;
        if (!string.IsNullOrWhiteSpace(playlist.CoverUrl))
        {
            DetailPlaylistCover.Source = new BitmapImage(new Uri(playlist.CoverUrl));
        }
        else
        {
            DetailPlaylistCover.Source = null;
        }

        bool isUserPlaylist = !playlist.IsFond
            && _playlistDetailSourcePage != "discover"
            && !_discoverPlaylists.Any(p => p.Id == playlist.Id)
            && _userPlaylists.Any(p => p.Id == playlist.Id);
        DeleteDetailPlaylistButton.Visibility = isUserPlaylist ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            LibLoadingRing.Visibility = Visibility.Visible;
            LibLoadingText.Text = $"正在加载《{playlist.Name}》的歌曲...";

            // 短暂合并连续切换
            await Task.Delay(120, cts.Token);
            var songs = await _services.Playlists.GetPlaylistSongsAsync(playlist.Id, playlist.SourceType, cts.Token);
            if (!IsCurrent()) return null;
            var rows = new List<SongViewModel>(songs.Count);
            for (var i = 0; i < songs.Count; i++)
            {
                rows.Add(new SongViewModel(songs[i], _services.Favorites.IsFavorite(songs[i].Id)));
                if (i % 100 == 99)
                {
                    await Task.Delay(1, cts.Token);
                    if (!IsCurrent()) return null;
                }
            }
            if (!IsCurrent()) return null;
            _detailPlaylistSongs = new ObservableCollection<SongViewModel>(rows);
            DetailPlaylistSongsList.ItemsSource = _detailPlaylistSongs;
            DetailPlaylistSubtitle.Text = $"{songs.Count} 首歌曲" + (string.IsNullOrWhiteSpace(playlist.CreatorName) ? "" : $" · {playlist.CreatorName}");
            return songs;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _services.Log.Write($"load playlist detail songs error: {ex.Message}");
            if (IsCurrent()) DetailPlaylistSubtitle.Text = "加载失败，请重新打开歌单重试";
            return null;
        }
        finally
        {
            if (version == _playlistLoadVersion)
            {
                LibLoadingRing.Visibility = Visibility.Collapsed;
                _playlistLoadCts = null;
            }
            cts.Dispose();
        }
    }

    private void PlaylistDetailBack_Click(object sender, RoutedEventArgs e) => GoBackInLibrary();

    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var title = await PromptInputAsync("新建歌单", "请输入歌单名称", "创建");
        if (string.IsNullOrWhiteSpace(title)) return;

        try
        {
            DismissLibResultPillInstant();
            LibLoadingRing.Visibility = Visibility.Visible;
            LibLoadingText.Text = $"正在创建歌单「{title}」...";

            var pl = await _services.Playlists.CreatePlaylistAsync(title, _lifetime.Token);
            ShowLibResultPill($"歌单「{pl.Name}」创建成功", LibResultType.Success);

            await RefreshAllPlaylistsAsync();
            Navigate($"library:playlist:{pl.Id}");
            await OpenPlaylistDetailAsync(pl);
        }
        catch (Exception ex)
        {
            ShowLibResultPill($"创建歌单失败: {ex.Message}", LibResultType.Error);
        }
        finally
        {
            LibLoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async void DeleteDetailPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is null) return;
        var pl = _selectedPlaylist;

        var confirmed = await ConfirmAsync("删除歌单", $"确定要删除歌单「{pl.Name}」吗？此操作无法撤销。", "删除");
        if (!confirmed) return;

        try
        {
            DismissLibResultPillInstant();
            LibLoadingRing.Visibility = Visibility.Visible;
            LibLoadingText.Text = $"正在删除歌单「{pl.Name}」...";

            var success = await _services.Playlists.DeletePlaylistAsync(pl.Id, _lifetime.Token);
            if (success)
            {
                ShowLibResultPill($"已删除歌单「{pl.Name}」", LibResultType.Success);
                await RefreshAllPlaylistsAsync();
                GoBackInLibrary();
            }
            else
            {
                ShowLibResultPill("删除歌单失败", LibResultType.Error);
            }
        }
        catch (Exception ex)
        {
            ShowLibResultPill($"删除歌单失败: {ex.Message}", LibResultType.Error);
        }
        finally
        {
            LibLoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void UserPlaylist_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Playlist pl)
        {
            _playlistDetailSourcePage = "library";
            NavigateLibrary($"library:playlist:{pl.Id}", isForward: true);
        }
    }

    private void PlayAllDetailPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _ = PlaySongListAsync(_detailPlaylistSongs.Select(s => s.Song).ToList(), 0, navigateToPlaying: false);
    }

    private void DetailPlaylistSong_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SongViewModel song)
        {
            var list = _detailPlaylistSongs.Select(s => s.Song).ToList();
            var idx = list.FindIndex(s => s.Id == song.Id);
            _ = PlaySongListAsync(list, Math.Max(0, idx));
        }
    }

    private void PlayDetailSong_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id })
        {
            var list = _detailPlaylistSongs.Select(s => s.Song).ToList();
            var idx = list.FindIndex(s => s.Id == id);
            _ = PlaySongListAsync(list, Math.Max(0, idx));
        }
    }

}
