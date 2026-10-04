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
    private async void FavoriteSong_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var vm = element.DataContext as SongViewModel ?? element.Tag as SongViewModel;
        if (vm is null) return;

        var isNowFavorite = await _services.Favorites.ToggleFavoriteAsync(vm.Song);
        vm.IsFavorite = isNowFavorite;

        if (_currentPage == "library")
        {
            var actionText = isNowFavorite ? "已添加到我喜欢的音乐" : "已从我喜欢的音乐中移除";
            ShowLibResultPill($"《{vm.Name}》{actionText}", isNowFavorite ? LibResultType.Success : LibResultType.Info);
        }
    }

    private async void OnFavoritesChanged()
    {
        foreach (var vm in _discoverSongs)
        {
            vm.IsFavorite = _services.Favorites.IsFavorite(vm.Id);
        }
        foreach (var vm in _recentSongs)
        {
            vm.IsFavorite = _services.Favorites.IsFavorite(vm.Id);
        }
        foreach (var vm in _detailPlaylistSongs)
        {
            vm.IsFavorite = _services.Favorites.IsFavorite(vm.Id);
        }
        foreach (var vm in _queueSongs)
        {
            vm.IsFavorite = _services.Favorites.IsFavorite(vm.Id);
        }
        foreach (var vm in _search.Results)
        {
            vm.IsFavorite = _services.Favorites.IsFavorite(vm.Id);
        }

        var favoriteSongs = await _services.Favorites.GetFavoriteSongsAsync();
        _fondSongs.Clear();
        foreach (var s in favoriteSongs)
        {
            _fondSongs.Add(new SongViewModel(s, isFavorite: true));
        }
        FondCount.Text = $"{favoriteSongs.Count} 首歌曲";
        LibRootFondSubtitle.Text = $"{favoriteSongs.Count} 首歌曲";

        UpdatePlayingFavoriteUi();
    }

    private static readonly SolidColorBrush PlayingFavoritedBrush = new(Windows.UI.Color.FromArgb(255, 235, 71, 71));

    private async void PlayingFavorite_Click(object sender, RoutedEventArgs e)
    {
        var song = _services.Coordinator.CurrentSong;
        if (song is null || song.Id <= 0) return;

        var isNowFavorite = await _services.Favorites.ToggleFavoriteAsync(song);
        UpdatePlayingFavoriteUi();

        var actionText = isNowFavorite ? "已添加到我喜欢的音乐" : "已从我喜欢的音乐中移除";
        ShowLibResultPill($"《{song.Name}》{actionText}", isNowFavorite ? LibResultType.Success : LibResultType.Info);
    }

    private void UpdatePlayingFavoriteUi()
    {
        var song = _services.Coordinator.CurrentSong;
        if (song is null || song.Id <= 0)
        {
            PlayingFavoriteButton.IsEnabled = false;
            PlayingFavoriteIcon.Glyph = "\uEB51";
            PlayingFavoriteIcon.Foreground = ThemeHelper.GetFavoriteUncheckedBrush();
            ToolTipService.SetToolTip(PlayingFavoriteButton, "添加收藏");
            return;
        }

        var isFav = _services.Favorites.IsFavorite(song.Id);
        PlayingFavoriteButton.IsEnabled = true;
        PlayingFavoriteIcon.Glyph = isFav ? "\uEB52" : "\uEB51";
        PlayingFavoriteIcon.Foreground = isFav ? PlayingFavoritedBrush : ThemeHelper.GetFavoriteUncheckedBrush();
        ToolTipService.SetToolTip(PlayingFavoriteButton, isFav ? "取消收藏" : "添加收藏");
    }

    private void SongMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var vm = element.DataContext as SongViewModel ?? element.Tag as SongViewModel;
        if (vm is null) return;

        var parentList = FindVisualParent<ListView>(element);
        string context = "general";
        if (ReferenceEquals(parentList, FondSongsList)) context = "fond";
        else if (ReferenceEquals(parentList, DetailPlaylistSongsList)) context = "playlist";
        else if (ReferenceEquals(parentList, QueueListView)) context = "queue";
        else if (ReferenceEquals(parentList, RecentSongsList)) context = "recent";
        else if (ReferenceEquals(parentList, SearchResults)) context = "search";
        else if (ReferenceEquals(parentList, DiscoverSongsList)) context = "discover";

        ShowSongMoreMenu(element, vm.Song, vm, context);
    }

    private void PlayingMore_Click(object sender, RoutedEventArgs e)
    {
        var song = _services.Coordinator.CurrentSong;
        if (song is null) return;
        ShowSongMoreMenu(PlayingMoreButton, song, null, "playing");
    }

    private void ShowSongMoreMenu(FrameworkElement anchor, Song song, SongViewModel? vm, string context)
    {
        var flyout = new MenuFlyout();

        var addToPlaylistItem = new MenuFlyoutSubItem
        {
            Text = "添加到歌单",
            Icon = new FontIcon { Glyph = "\uE710" }
        };

        var fondItem = new MenuFlyoutItem
        {
            Text = "我喜欢的音乐",
            Icon = new FontIcon { Glyph = "\uEB51" }
        };
        fondItem.Click += async (_, _) =>
        {
            await _services.Favorites.AddFavoriteAsync(song);
            if (vm != null) vm.IsFavorite = true;
            ShowLibResultPill($"已将《{song.Name}》添加到我喜欢的音乐", LibResultType.Success);
        };
        addToPlaylistItem.Items.Add(fondItem);

        if (_userPlaylists.Count > 0)
        {
            addToPlaylistItem.Items.Add(new MenuFlyoutSeparator());
            foreach (var pl in _userPlaylists)
            {
                var playlistItem = new MenuFlyoutItem
                {
                    Text = pl.Name,
                    Icon = new FontIcon { Glyph = "\uE8D6" }
                };
                var targetPl = pl;
                playlistItem.Click += async (_, _) =>
                {
                    try
                    {
                        var ok = await _services.Playlists.AddSongToPlaylistAsync(targetPl.Id, song, _lifetime.Token);
                        if (ok)
                        {
                            ShowLibResultPill($"已将《{song.Name}》添加到歌单「{targetPl.Name}」", LibResultType.Success);
                            if (_selectedPlaylist?.Id == targetPl.Id)
                            {
                                _detailPlaylistSongs.Add(new SongViewModel(song, _services.Favorites.IsFavorite(song.Id)));
                                DetailPlaylistSubtitle.Text = $"{_detailPlaylistSongs.Count} 首歌曲" + (string.IsNullOrWhiteSpace(targetPl.CreatorName) ? "" : $" · {targetPl.CreatorName}");
                            }
                        }
                        else
                        {
                            ShowLibResultPill("添加到歌单失败，请稍后重试", LibResultType.Warning);
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowLibResultPill($"添加到歌单失败: {ex.Message}", LibResultType.Warning);
                    }
                };
                addToPlaylistItem.Items.Add(playlistItem);
            }
        }
        else
        {
            _ = LoadUserPlaylistsAsync();
        }

        flyout.Items.Add(addToPlaylistItem);

        var addToQueueItem = new MenuFlyoutItem
        {
            Text = "添加到播放队列",
            Icon = new FontIcon { Glyph = "\uE8DA" }
        };
        addToQueueItem.Click += async (_, _) =>
        {
            await _services.Coordinator.EnqueueAsync(song, playNext: false, _lifetime.Token);
            ShowLibResultPill($"已将《{song.Name}》添加到播放队列", LibResultType.Success);
        };
        flyout.Items.Add(addToQueueItem);

        var playNextItem = new MenuFlyoutItem
        {
            Text = "下一首播放",
            Icon = new FontIcon { Glyph = "\uE895" }
        };
        playNextItem.Click += async (_, _) =>
        {
            await _services.Coordinator.EnqueueAsync(song, playNext: true, _lifetime.Token);
            ShowLibResultPill($"已将《{song.Name}》设为下一首播放", LibResultType.Success);
        };
        flyout.Items.Add(playNextItem);

        MenuFlyoutItem? deleteItem = null;

        if (context == "fond")
        {
            deleteItem = new MenuFlyoutItem
            {
                Text = "从我喜欢的音乐移除",
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            deleteItem.Click += async (_, _) =>
            {
                await _services.Favorites.RemoveFavoriteAsync(song.Id);
                if (vm != null) _fondSongs.Remove(vm);
                ShowLibResultPill($"已从我喜欢的音乐中移除《{song.Name}》", LibResultType.Info);
            };
        }
        else if (context == "playlist" && _selectedPlaylist != null
                 && !_discoverPlaylists.Any(p => p.Id == _selectedPlaylist.Id)
                 && _userPlaylists.Any(p => p.Id == _selectedPlaylist.Id))
        {
            var curPl = _selectedPlaylist;
            deleteItem = new MenuFlyoutItem
            {
                Text = $"从歌单「{curPl.Name}」删除",
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            deleteItem.Click += async (_, _) =>
            {
                try
                {
                    var ok = await _services.Playlists.RemoveSongFromPlaylistAsync(curPl.Id, song.Id, _lifetime.Token);
                    if (ok)
                    {
                        if (vm != null) _detailPlaylistSongs.Remove(vm);
                        DetailPlaylistSubtitle.Text = $"{_detailPlaylistSongs.Count} 首歌曲" + (string.IsNullOrWhiteSpace(curPl.CreatorName) ? "" : $" · {curPl.CreatorName}");
                        ShowLibResultPill($"已从歌单「{curPl.Name}」中移除《{song.Name}》", LibResultType.Info);
                    }
                    else
                    {
                        ShowLibResultPill("从歌单删除失败，请稍后重试", LibResultType.Warning);
                    }
                }
                catch (Exception ex)
                {
                    ShowLibResultPill($"从歌单删除失败: {ex.Message}", LibResultType.Warning);
                }
            };
        }
        else if (context == "queue")
        {
            deleteItem = new MenuFlyoutItem
            {
                Text = "从播放队列移除",
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            deleteItem.Click += async (_, _) =>
            {
                if (vm != null)
                {
                    var idx = _queueSongs.IndexOf(vm);
                    if (idx >= 0)
                    {
                        await _services.Coordinator.RemoveAtAsync(idx, _lifetime.Token);
                        ShowLibResultPill($"已从播放队列移除《{song.Name}》", LibResultType.Info);
                    }
                }
            };
        }
        else if (_services.Favorites.IsFavorite(song.Id))
        {
            deleteItem = new MenuFlyoutItem
            {
                Text = "从我喜欢的音乐移除",
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            deleteItem.Click += async (_, _) =>
            {
                await _services.Favorites.RemoveFavoriteAsync(song.Id);
                if (vm != null) vm.IsFavorite = false;
                ShowLibResultPill($"已从我喜欢的音乐中移除《{song.Name}》", LibResultType.Info);
            };
        }

        if (deleteItem != null)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(deleteItem);
        }

        flyout.ShowAt(anchor);
    }

}
