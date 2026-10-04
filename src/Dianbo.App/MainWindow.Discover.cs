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
    private readonly ObservableCollection<SongViewModel> _discoverSongs = [];
    private readonly ObservableCollection<Playlist> _discoverPlaylists = [];
    private int _discoverSongsPage = 1;
    private bool _discoverLoaded;

    private const int DiscoverRefreshCooldownMs = 1500;
    private bool _isRefreshingDiscoverSongs;
    private bool _isRefreshingDiscoverPlaylists;
    private bool _isLoadingDiscoverSongs;
    private bool _isLoadingDiscoverPlaylists;

    private async Task LoadDiscoverContentAsync(bool forceRefresh = false)
    {
        if (_discoverLoaded && !forceRefresh) return;

        await Task.WhenAll(
            LoadDiscoverSongsAsync(shuffle: false),
            LoadDiscoverPlaylistsAsync(shuffle: false)
        );
        if (_discoverSongs.Count > 0 || _discoverPlaylists.Count > 0)
        {
            _discoverLoaded = true;
        }
    }

    private async Task LoadDiscoverSongsAsync(bool shuffle = false)
    {
        if (_isLoadingDiscoverSongs) return;
        _isLoadingDiscoverSongs = true;

        try
        {
            DiscoverSongsRing.Visibility = Visibility.Visible;
            DiscoverSongsRing.IsActive = true;
            if (shuffle)
            {
                _discoverSongsPage = Random.Shared.Next(1, 26);
            }

            var songs = await _services.Api.GetRecommendSongsAsync(_discoverSongsPage, 6, _lifetime.Token);
            if (songs.Count == 0 && _discoverSongsPage != 1)
            {
                _discoverSongsPage = 1;
                songs = await _services.Api.GetRecommendSongsAsync(1, 6, _lifetime.Token);
            }

            if (songs.Count > 0)
            {
                DiscoverOfflineInfoBar.IsOpen = false;
                _discoverSongs.Clear();
                foreach (var s in songs)
                {
                    _discoverSongs.Add(new SongViewModel(s, _services.Favorites.IsFavorite(s.Id)));
                }
            }
            else if (_discoverSongs.Count == 0)
            {
                if (_services.Auth.IsSignedIn)
                    ShowDiscoverUnavailable("暂无推荐歌曲", "当前没有可显示的推荐歌曲，请稍后重试。");
                else
                    ShowDiscoverUnavailable("登录后查看推荐", "登录账号后即可查看推荐歌曲。您仍可使用搜索和本地音乐。");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _services.Log.Write($"load discover songs failed: {ex.Message}");
            if (_discoverSongs.Count == 0)
            {
                if (ex is BodianApiException apiEx && apiEx.IsPermissionDenied && !_services.Auth.IsSignedIn)
                    ShowDiscoverUnavailable("登录后查看推荐", "推荐歌曲需要登录账号。您仍可使用搜索和本地音乐。");
                else if (ex is HttpRequestException or TimeoutException or TaskCanceledException)
                    ShowDiscoverUnavailable("当前处于离线模式", "网络不可用，无法加载在线推荐内容。您可以前往“我的音乐”播放本地缓存的歌曲。");
                else
                    ShowDiscoverUnavailable("暂时无法加载推荐", "推荐服务暂时不可用，请稍后重试。");
            }
        }
        finally
        {
            DiscoverSongsRing.IsActive = false;
            DiscoverSongsRing.Visibility = Visibility.Collapsed;
            _isLoadingDiscoverSongs = false;
        }
    }

    private void ShowDiscoverUnavailable(string title, string message)
    {
        DiscoverOfflineInfoBar.Title = title;
        DiscoverOfflineInfoBar.Message = message;
        DiscoverOfflineInfoBar.IsOpen = true;
    }

    private async Task LoadDiscoverPlaylistsAsync(bool shuffle = false)
    {
        if (_isLoadingDiscoverPlaylists) return;
        _isLoadingDiscoverPlaylists = true;

        try
        {
            DiscoverPlaylistsRing.Visibility = Visibility.Visible;
            DiscoverPlaylistsRing.IsActive = true;

            var playlists = await _services.Api.GetRecommendPlaylistsAsync(6, _lifetime.Token);
            _discoverPlaylists.Clear();
            foreach (var pl in playlists)
            {
                _discoverPlaylists.Add(pl);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Log.Write($"load discover playlists failed: {ex.Message}");
        }
        finally
        {
            DiscoverPlaylistsRing.IsActive = false;
            DiscoverPlaylistsRing.Visibility = Visibility.Collapsed;
            _isLoadingDiscoverPlaylists = false;
        }
    }

    private async void RefreshDiscoverSongs_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingDiscoverSongs) return;
        _isRefreshingDiscoverSongs = true;

        RefreshDiscoverSongsBtn.IsEnabled = false;
        ToolTipService.SetToolTip(RefreshDiscoverSongsBtn, "换一批推荐歌曲（冷却中）");
        var retryBtn = sender as Button;
        if (retryBtn != null && retryBtn != RefreshDiscoverSongsBtn)
        {
            retryBtn.IsEnabled = false;
        }

        try
        {
            await LoadDiscoverSongsAsync(shuffle: true);
            if (_discoverPlaylists.Count == 0)
            {
                await LoadDiscoverPlaylistsAsync(shuffle: true);
            }

            await Task.Delay(DiscoverRefreshCooldownMs, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Log.Write($"refresh discover songs failed: {ex.Message}");
        }
        finally
        {
            _isRefreshingDiscoverSongs = false;
            RefreshDiscoverSongsBtn.IsEnabled = true;
            ToolTipService.SetToolTip(RefreshDiscoverSongsBtn, "换一批推荐歌曲");
            if (retryBtn != null)
            {
                retryBtn.IsEnabled = true;
            }
        }
    }

    private void PlayAllDiscoverSongs_Click(object sender, RoutedEventArgs e)
    {
        if (_discoverSongs.Count > 0)
        {
            _ = PlaySongListAsync(_discoverSongs.Select(s => s.Song).ToList(), 0, navigateToPlaying: false);
        }
    }

    private async void PlayRandomDiscoverSong_Click(object sender, RoutedEventArgs e)
    {
        PlayRandomDiscoverSongBtn.IsEnabled = false;
        try
        {
            await _services.Coordinator.StartRecommendationRadioAsync(_lifetime.Token);
            if (_services.Settings.AutoNavigateToNowPlayingOnPlay && _currentPage != "playing")
            {
                Navigate("playing");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Log.Write($"start recommendation radio failed: {ex.Message}");
            await ShowDialogAsync("提示", $"获取推荐歌曲失败：{ex.Message}");
        }
        finally
        {
            PlayRandomDiscoverSongBtn.IsEnabled = true;
        }
    }

    private void DiscoverSong_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SongViewModel vm)
        {
            var songs = _discoverSongs.Select(s => s.Song).ToList();
            var idx = songs.FindIndex(s => s.Id == vm.Id);
            if (idx >= 0)
            {
                _ = PlaySongListAsync(songs, idx);
            }
        }
    }

    private void PlayDiscoverSong_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long songId })
        {
            var songs = _discoverSongs.Select(s => s.Song).ToList();
            var idx = songs.FindIndex(s => s.Id == songId);
            if (idx >= 0)
            {
                _ = PlaySongListAsync(songs, idx);
            }
        }
    }

    private async void RefreshDiscoverPlaylists_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingDiscoverPlaylists) return;
        _isRefreshingDiscoverPlaylists = true;

        RefreshDiscoverPlaylistsBtn.IsEnabled = false;
        ToolTipService.SetToolTip(RefreshDiscoverPlaylistsBtn, "换一批精选歌单（冷却中）");

        try
        {
            await LoadDiscoverPlaylistsAsync(shuffle: true);
            await Task.Delay(DiscoverRefreshCooldownMs, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Log.Write($"refresh discover playlists failed: {ex.Message}");
        }
        finally
        {
            _isRefreshingDiscoverPlaylists = false;
            RefreshDiscoverPlaylistsBtn.IsEnabled = true;
            ToolTipService.SetToolTip(RefreshDiscoverPlaylistsBtn, "换一批精选歌单");
        }
    }

    private async void DiscoverPlaylist_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Playlist pl)
        {
            _playlistDetailSourcePage = "discover";
            _selectedPlaylist = pl;
            Navigate($"library:playlist:{pl.Id}");
            await OpenPlaylistDetailAsync(pl);
        }
    }

}
