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
    private bool _updatingSeekBar;
    private bool _isSeekDragging;
    /// 当前已加载的封面地址
    private string? _coverUrl;
    private long? _lastSnapshotSongId;
    private readonly ObservableCollection<SongViewModel> _queueSongs = [];
    private bool _syncingQueue;
    private int _lastQueueHighlightIndex = -2;
    private bool _queueRestored;

    private async Task PlaySongAsync(SongViewModel song)
    {
        // 播放队列来自当前搜索结果
        var queue = _search.Results.Select(item => item.Song).ToList();
        var index = queue.FindIndex(item => item.Id == song.Id);
        if (index < 0)
        {
            queue.Insert(0, song.Song);
            index = 0;
        }
        try
        {
            await _services.Coordinator.PlayQueueAsync(queue, index, _lifetime.Token);
            if (_services.Settings.AutoNavigateToNowPlayingOnPlay && _currentPage != "playing")
            {
                Navigate("playing");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _ = TogglePauseAsync();

    private async Task TogglePauseAsync()
    {
        try
        {
            await _services.Coordinator.TogglePauseAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("提示", $"暂停／继续失败：{exception.Message}");
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => _ = _services.Coordinator.PreviousAsync(_lifetime.Token);

    private void Next_Click(object sender, RoutedEventArgs e) => _ = _services.Coordinator.NextAsync(_lifetime.Token);

    private void SeekSlider_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => _isSeekDragging = true;

    private void SeekSlider_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isSeekDragging) return;
        _isSeekDragging = false;

        _ = _services.Coordinator.SeekAsync(TimeSpan.FromSeconds(SeekSlider.Value), _lifetime.Token);
    }

    private int _volumeBeforeMute = 60;

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_applyingSettings) return;
        if (_updatingSeekBar) return;

        var vol = (int)Math.Round(e.NewValue);
        if (VolumePercentText is not null) VolumePercentText.Text = $"{vol}%";
        if (VolumeIcon is not null) VolumeIcon.Glyph = vol == 0 ? "\uE74F" : (vol < 50 ? "\uE994" : "\uE767");

        if (Math.Abs(e.NewValue - _services.Coordinator.Volume) < 0.5) return;

        _services.Settings.Volume = vol;
        _ = _services.Coordinator.SetVolumeAsync(vol, _lifetime.Token);
        _services.SaveSettings();

    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Coordinator.Volume > 0)
        {
            _volumeBeforeMute = _services.Coordinator.Volume;
            _services.Settings.Volume = 0;
            _ = _services.Coordinator.SetVolumeAsync(0, _lifetime.Token);
            _services.SaveSettings();

            _applyingSettings = true;
            try
            {
                VolumeSlider.Value = 0;
                if (VolumePercentText is not null) VolumePercentText.Text = "0%";
                if (VolumeIcon is not null) VolumeIcon.Glyph = "\uE74F";
            }
            finally
            {
                _applyingSettings = false;
            }
        }
        else
        {
            var target = _volumeBeforeMute > 0 ? _volumeBeforeMute : 60;
            _services.Settings.Volume = target;
            _ = _services.Coordinator.SetVolumeAsync(target, _lifetime.Token);
            _services.SaveSettings();

            _applyingSettings = true;
            try
            {
                VolumeSlider.Value = target;
                if (VolumePercentText is not null) VolumePercentText.Text = $"{target}%";
                if (VolumeIcon is not null) VolumeIcon.Glyph = target < 50 ? "\uE994" : "\uE767";
            }
            finally
            {
                _applyingSettings = false;
            }
        }
    }

    private void PlayModeButton_Click(object sender, RoutedEventArgs e)
    {
        var nextMode = _services.Coordinator.PlayMode switch
        {
            PlaybackMode.RepeatAll => PlaybackMode.RepeatOne,
            PlaybackMode.RepeatOne => PlaybackMode.Shuffle,
            PlaybackMode.Shuffle => PlaybackMode.Sequential,
            PlaybackMode.Sequential => PlaybackMode.RepeatAll,
            _ => PlaybackMode.RepeatAll
        };
        ApplyPlayMode(nextMode);
    }

    private void QueuePlayModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string tag)
        {
            var mode = tag switch
            {
                "Sequential" => PlaybackMode.Sequential,
                "RepeatOne" => PlaybackMode.RepeatOne,
                "Shuffle" => PlaybackMode.Shuffle,
                _ => PlaybackMode.RepeatAll
            };
            ApplyPlayMode(mode);
        }
    }

    private void ApplyPlayMode(PlaybackMode mode)
    {
        _services.Coordinator.PlayMode = mode;
        var modeString = mode switch
        {
            PlaybackMode.Sequential => "Sequential",
            PlaybackMode.RepeatOne => "RepeatOne",
            PlaybackMode.Shuffle => "Shuffle",
            _ => "RepeatAll"
        };
        _services.Settings.PlayMode = modeString;
        _services.SaveSettings();
        UpdatePlayModeUi(mode);
    }

    private void UpdatePlayModeUi(PlaybackMode mode)
    {
        var (glyph, tip) = mode switch
        {
            PlaybackMode.Sequential => ("\uF5E7", "顺序播放"),
            PlaybackMode.RepeatAll => ("\uE8EE", "列表循环"),
            PlaybackMode.RepeatOne => ("\uE8ED", "单曲循环"),
            PlaybackMode.Shuffle => ("\uE8B1", "随机播放"),
            _ => ("\uE8EE", "列表循环")
        };

        if (PlayModeIcon is not null)
        {
            PlayModeIcon.Glyph = glyph;
            ToolTipService.SetToolTip(PlayModeButton, tip);
        }

        if (QueuePlayModeIcon is not null && QueuePlayModeText is not null)
        {
            QueuePlayModeIcon.Glyph = glyph;
            QueuePlayModeText.Text = tip;
        }
    }

    private void SyncQueueUi()
    {
        var queue = _services.Coordinator.Queue;
        var currentIndex = _services.Coordinator.QueueIndex;

        _syncingQueue = true;
        try
        {
            var needRebuild = _queueSongs.Count != queue.Count;
            if (!needRebuild)
            {
                for (var i = 0; i < queue.Count; i++)
                {
                    if (_queueSongs[i].Id != queue[i].Id)
                    {
                        needRebuild = true;
                        break;
                    }
                }
            }

            if (needRebuild)
            {
                _queueSongs.Clear();
                for (var i = 0; i < queue.Count; i++)
                {
                    var s = queue[i];
                    var vm = new SongViewModel(s, _services.Favorites.IsFavorite(s.Id))
                    {
                        QueueIndex = i,
                        IsCurrentSong = (i == currentIndex)
                    };
                    _queueSongs.Add(vm);
                }
            }
            else
            {
                for (var i = 0; i < _queueSongs.Count; i++)
                {
                    _queueSongs[i].QueueIndex = i;
                    _queueSongs[i].IsCurrentSong = (i == currentIndex);
                    _queueSongs[i].IsFavorite = _services.Favorites.IsFavorite(_queueSongs[i].Id);
                }
            }

            if (_queueSongs.Count == 0)
            {
                QueueEmptyCard.Visibility = Visibility.Visible;
                QueueContentArea.Visibility = Visibility.Collapsed;
                QueueCountText.Text = "共 0 首歌曲";
                QueuePlayingDot.Visibility = Visibility.Collapsed;
                QueueCurrentPlayingText.Visibility = Visibility.Collapsed;
            }
            else
            {
                QueueEmptyCard.Visibility = Visibility.Collapsed;
                QueueContentArea.Visibility = Visibility.Visible;
                QueueCountText.Text = $"共 {_queueSongs.Count} 首歌曲";

                if (currentIndex >= 0 && currentIndex < queue.Count)
                {
                    QueuePlayingDot.Visibility = Visibility.Visible;
                    QueueCurrentPlayingText.Visibility = Visibility.Visible;
                    QueueCurrentPlayingText.Text = $"正在播放：{queue[currentIndex].Name}";
                    QueueListView.SelectedIndex = currentIndex;
                }
                else
                {
                    QueuePlayingDot.Visibility = Visibility.Collapsed;
                    QueueCurrentPlayingText.Visibility = Visibility.Collapsed;
                    QueueListView.SelectedIndex = -1;
                }
            }
        }
        finally
        {
            _syncingQueue = false;
        }

        UpdatePlayModeUi(_services.Coordinator.PlayMode);
    }

    private void SyncQueuePlayingHighlight()
    {
        var currentIndex = _services.Coordinator.QueueIndex;
        if (currentIndex == _lastQueueHighlightIndex) return;
        _lastQueueHighlightIndex = currentIndex;

        for (var i = 0; i < _queueSongs.Count; i++)
        {
            _queueSongs[i].IsCurrentSong = (i == currentIndex);
        }

        var queue = _services.Coordinator.Queue;
        if (currentIndex >= 0 && currentIndex < queue.Count)
        {
            QueuePlayingDot.Visibility = Visibility.Visible;
            QueueCurrentPlayingText.Visibility = Visibility.Visible;
            QueueCurrentPlayingText.Text = $"正在播放：{queue[currentIndex].Name}";
            if (!_syncingQueue)
            {
                QueueListView.SelectedIndex = currentIndex;
            }
        }
        else
        {
            QueuePlayingDot.Visibility = Visibility.Collapsed;
            QueueCurrentPlayingText.Visibility = Visibility.Collapsed;
        }
    }

    private void SaveQueueState()
    {
        if (!_queueRestored) return;
        try
        {
            var queue = _services.Coordinator.Queue;
            if (queue.Count == 0)
            {
                _services.QueueStore.Save(new QueueStateRecord());
                return;
            }

            var snapshot = _services.Coordinator.Snapshot;
            var record = new QueueStateRecord
            {
                Songs = [.. queue],
                CurrentIndex = _services.Coordinator.QueueIndex,
                PositionSeconds = 0,
                DurationSeconds = snapshot.Duration.TotalSeconds,
                PlayMode = _services.Coordinator.PlayMode.ToString()
            };
            _services.QueueStore.Save(record);
        }
        catch (Exception exception)
        {
            _services.Log.Write($"save queue state error: {exception.Message}");
        }
    }

    private void RestoreQueueState()
    {
        try
        {
            var state = _services.QueueStore.Load();
            if (state.Songs == null || state.Songs.Count == 0) return;

            var mode = Enum.TryParse<PlaybackMode>(state.PlayMode, out var parsedMode)
                ? parsedMode
                : (PlaybackMode?)null;

            _services.Coordinator.RestoreState(
                state.Songs,
                state.CurrentIndex,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(Math.Max(0, state.DurationSeconds)),
                mode);

            SyncQueueUi();
            ApplySnapshot(_services.Coordinator.Snapshot);

            if (_services.Settings.AutoPlayOnLaunch && _services.Coordinator.CurrentSong is not null)
            {
                _services.Log.Write("auto playing restored song on launch");
                _ = _services.Coordinator.PlayAtAsync(_services.Coordinator.QueueIndex, _lifetime.Token);
            }
        }
        catch (Exception exception)
        {
            _services.Log.Write($"queue restore failed: {exception.Message}");
        }
        finally
        {
            _queueRestored = true;
        }
    }

    private async void QueueSong_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SongViewModel song)
        {
            await _services.Coordinator.PlayAtAsync(song.QueueIndex, _lifetime.Token);
            if (_services.Settings.AutoNavigateToNowPlayingOnPlay && _currentPage != "playing")
            {
                Navigate("playing");
            }
        }
    }

    private async void PlayQueueSong_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SongViewModel song })
        {
            await _services.Coordinator.PlayAtAsync(song.QueueIndex, _lifetime.Token);
            if (_services.Settings.AutoNavigateToNowPlayingOnPlay && _currentPage != "playing")
            {
                Navigate("playing");
            }
        }
    }

    private async void RemoveQueueSong_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SongViewModel song })
        {
            await _services.Coordinator.RemoveAtAsync(song.QueueIndex, _lifetime.Token);
        }
    }

    private async void QueueClear_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Coordinator.Queue.Count == 0) return;
        await _services.Coordinator.ClearQueueAsync(_lifetime.Token);
    }

    private void QueueLocateCurrent_Click(object sender, RoutedEventArgs e)
    {
        var idx = _services.Coordinator.QueueIndex;
        if (idx >= 0 && idx < _queueSongs.Count)
        {
            QueueListView.SelectedIndex = idx;
            QueueListView.ScrollIntoView(_queueSongs[idx]);
        }
    }

    private void PlayingQueue_Click(object sender, RoutedEventArgs e)
    {
        Navigate("queue");
    }

    private async void QueueContextPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SongViewModel song })
        {
            await _services.Coordinator.PlayAtAsync(song.QueueIndex, _lifetime.Token);
            if (_services.Settings.AutoNavigateToNowPlayingOnPlay && _currentPage != "playing")
            {
                Navigate("playing");
            }
        }
    }

    private async void QueueContextRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SongViewModel song })
        {
            await _services.Coordinator.RemoveAtAsync(song.QueueIndex, _lifetime.Token);
        }
    }

    private void PlayShuffleFond_Click(object sender, RoutedEventArgs e)
    {
        _ = PlaySongListShuffleAsync(_fondSongs.Select(s => s.Song).ToList());
    }

    private void PlayShuffleRecent_Click(object sender, RoutedEventArgs e)
    {
        _ = PlaySongListShuffleAsync(_recentSongs.Select(s => s.Song).ToList());
    }

    private void PlayShuffleDetailPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _ = PlaySongListShuffleAsync(_detailPlaylistSongs.Select(s => s.Song).ToList());
    }

    private async Task PlaySongListShuffleAsync(IReadOnlyList<Song> songs)
    {
        if (songs.Count == 0) return;
        ApplyPlayMode(PlaybackMode.Shuffle);
        var startIndex = Random.Shared.Next(0, songs.Count);
        await PlaySongListAsync(songs, startIndex);
    }


    private void ApplySnapshot(PlaybackSnapshot snapshot)
    {
        if (_closing || snapshot.Generation != _services.Coordinator.Snapshot.Generation) return;
        var song = _services.Coordinator.CurrentSong;
        if (song?.Id != _lastSnapshotSongId)
        {
            _lastSnapshotSongId = song?.Id;
            _lyricAutoResumeTimer?.Stop();
            _isManualLyricBrowsing = false;
            if (LyricsFollowStatusText is not null)
            {
                LyricsFollowStatusText.Text = "跟随中";
            }
        }
        var title = song?.Name ?? "点波音乐";
        var artist = song is null ? "发现好音乐，享受当下" : (string.IsNullOrWhiteSpace(song.Artist) ? "未知歌手" : song.Artist);
        NowPlayingTitle.Text = title;
        NowPlayingArtist.Text = artist;
        LyricsHeader.Text = song?.Name ?? string.Empty;

        if (song is not null && !string.IsNullOrWhiteSpace(song.Album))
        {
            NowPlayingAlbum.Text = $"专辑：《{song.Album}》";
            NowPlayingAlbum.Visibility = Visibility.Visible;
        }
        else
        {
            NowPlayingAlbum.Visibility = Visibility.Collapsed;
        }

        var hasSong = snapshot.SongId is not null && snapshot.State is not (PlaybackState.Idle or PlaybackState.Stopping);

        NowPlayingQualityBadge.Visibility = hasSong ? Visibility.Visible : Visibility.Collapsed;
        if (hasSong)
        {
            NowPlayingQualityText.Text = snapshot.AudioSource?.DisplayQuality ?? "标准品质 · MP3";
        }

        var isBuffering = hasSong && snapshot.State is PlaybackState.Resolving or PlaybackState.Loading or PlaybackState.Seeking;
        NowPlayingBufferingOverlay.Visibility = isBuffering ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrEmpty(snapshot.Error))
        {
            NowPlayingInfoBar.Message = snapshot.Error;
            NowPlayingInfoBar.IsOpen = true;
        }
        else
        {
            NowPlayingInfoBar.IsOpen = false;
        }

        PreviousButton.IsEnabled = hasSong;
        NextButton.IsEnabled = hasSong;
        PlayPauseButton.IsEnabled = hasSong;

        PlayingMoreButton.IsEnabled = hasSong;
        UpdatePlayingFavoriteUi();
        PlayPauseIcon.Glyph = snapshot.IsPaused ? "\uE768" : "\uE769";
        ToolTipService.SetToolTip(PlayPauseButton, snapshot.IsPaused ? "播放" : "暂停");

        var duration = snapshot.Duration.TotalSeconds;
        _updatingSeekBar = true;
        _applyingSettings = true;
        try
        {
            SeekSlider.IsEnabled = hasSong && duration > 0;
            SeekSlider.Maximum = duration > 0 ? duration : 100;
            if (!_isSeekDragging) SeekSlider.Value = Math.Clamp(snapshot.Position.TotalSeconds, 0, SeekSlider.Maximum);
            VolumeSlider.Value = snapshot.Volume;
        }
        finally
        {
            _applyingSettings = false;
            _updatingSeekBar = false;
        }
        PositionText.Text = Format(snapshot.Position);
        DurationText.Text = duration > 0 ? Format(snapshot.Duration) : "0:00";
        VolumePercentText.Text = $"{snapshot.Volume}%";
        VolumeIcon.Glyph = snapshot.Volume == 0 ? "\uE74F" : (snapshot.Volume < 50 ? "\uE994" : "\uE767");
        UpdatePlayModeUi(_services.Coordinator.PlayMode);
        SyncQueuePlayingHighlight();



        var cover = song?.CoverUrl;
        if (!string.Equals(cover, _coverUrl, StringComparison.Ordinal))
        {
            _coverUrl = cover;
            if (!string.IsNullOrWhiteSpace(cover)) _services.Log.Write("cover: loaded");
            NowPlayingCover.Source = CreateCoverSource(cover);
        }
        CoverPlaceholderIcon.Visibility = string.IsNullOrWhiteSpace(cover) ? Visibility.Visible : Visibility.Collapsed;

        UpdateLyricsVisibility(hasSong);
        // EOF can report position zero after mpv unloads the file. Do not start
        // scrolling the old lyrics back to the beginning while switching songs.
        if (snapshot.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Seeking)
            UpdateCurrentLyric(snapshot.Position);
        SyncTaskbarLyrics();
        UpdateSystemMediaControls(snapshot);
    }

    private static string Format(TimeSpan value) => $"{(int)value.TotalMinutes}:{value.Seconds:00}";


    private static ImageSource? CreateCoverSource(string? cover)
    {
        if (string.IsNullOrWhiteSpace(cover)) return null;
        if (!Uri.TryCreate(cover, UriKind.Absolute, out var uri)) return null;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 560 };
            bitmap.UriSource = uri;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

}
