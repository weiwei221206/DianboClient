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
    private static readonly string[] SettingsPages =
    {
        "settings", "settings-account", "settings-general", "settings-playback", "settings-lyrics",
        "settings-main-lyrics", "settings-taskbar-lyrics", "settings-appearance", "settings-storage", "settings-about"
    };

    private string _currentSettingsPage = "settings";
    private readonly Stack<string> _settingsHistory = new();
    private Storyboard? _settingsTransitionStoryboard;
    private FrameworkElement? _activeOutgoingPanel;
    private TranslateTransform? _activeOutgoingTransform;
    private bool _applyingSettings = true;

    private void ApplySettingsToUi()
    {
        _applyingSettings = true;
        try
        {
            VolumeSlider.Value = _services.Settings.Volume;
            VolumePercentText.Text = $"{_services.Settings.Volume}%";
            if (VolumeIcon is not null)
            {
                VolumeIcon.Glyph = _services.Settings.Volume == 0 ? "\uE74F" : (_services.Settings.Volume < 50 ? "\uE994" : "\uE767");
            }
            _volumeBeforeMute = _services.Settings.Volume > 0 ? _services.Settings.Volume : 60;
            _ = _services.Coordinator.SetVolumeAsync(_services.Settings.Volume, _lifetime.Token);
            UpdateTranslationButtonUi(_services.Settings.ShowTranslation);
            ShowTranslationToggle.IsOn = _services.Settings.ShowTranslation;
            AutoFollowLyricsToggle.IsOn = _services.Settings.AutoFollowLyrics;
            LyricFollowDelaySlider.Value = Math.Clamp(_services.Settings.LyricFollowDelaySeconds, 2, 10);
            LyricFollowDelayText.Text = $"{Math.Clamp(_services.Settings.LyricFollowDelaySeconds, 2, 10)} 秒";
            LyricFollowDelaySlider.IsEnabled = _services.Settings.AutoFollowLyrics;
            ClickLyricToSeekToggle.IsOn = _services.Settings.ClickLyricToSeek;
            LyricsList.IsItemClickEnabled = _services.Settings.ClickLyricToSeek;
            AutoNavigateToNowPlayingToggle.IsOn = _services.Settings.AutoNavigateToNowPlayingOnPlay;
            SkipUnplayableToggle.IsOn = _services.Settings.AutoSkipUnplayable;
            RetryToggle.IsOn = _services.Settings.RetryOnceOnFailure;
            UseSystemMediaKeysToggle.IsOn = _services.Settings.UseSystemMediaKeys;
            _services.Coordinator.PlayMode = _services.Settings.PlayMode switch
            {
                "Sequential" => PlaybackMode.Sequential,
                "RepeatOne" => PlaybackMode.RepeatOne,
                "Shuffle" => PlaybackMode.Shuffle,
                _ => PlaybackMode.RepeatAll
            };
            UpdatePlayModeUi(_services.Coordinator.PlayMode);

            AudioQualityCombo.SelectedIndex = _services.Settings.PreferredQuality switch
            {
                "128kmp3" => 2,
                "320kmp3" => 1,
                _ => 0
            };
            _services.Coordinator.PreferredQuality = _services.Settings.PreferredQuality;
            UpdateAudioQualityUi(_services.Settings.PreferredQuality);

            AutoStartToggle.IsOn = AutoStartHelper.IsAutoStartEnabled();
            SilentAutoStartToggle.IsOn = _services.Settings.SilentAutoStart;
            AutoPlayOnLaunchToggle.IsOn = _services.Settings.AutoPlayOnLaunch;
            CloseBehaviorCombo.SelectedIndex = string.Equals(_services.Settings.CloseBehavior, "Exit", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            ThemeCombo.SelectedIndex = _services.Settings.Theme switch
            {
                "Light" => 0,
                "Dark" => 1,
                _ => 2
            };
            ApplyTheme(_services.Settings.Theme);
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        var tag = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default";
        _services.Settings.Theme = tag;
        _services.SaveSettings();
        ApplyTheme(tag);
    }

    private void ApplyTheme(string theme)
    {
        var elementTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        Root.RequestedTheme = elementTheme;
        ThemeHelper.ActualTheme = Root.ActualTheme;
        UpdateTitleBarTheme();
        RefreshThemeOnActiveViews();
    }

    private void RefreshThemeOnActiveViews()
    {
        foreach (var song in _queueSongs) song.RefreshThemeBrushes();
        foreach (var song in _discoverSongs) song.RefreshThemeBrushes();
        foreach (var song in _recentSongs) song.RefreshThemeBrushes();
        foreach (var song in _fondSongs) song.RefreshThemeBrushes();
        foreach (var song in _detailPlaylistSongs) song.RefreshThemeBrushes();
        foreach (var song in _search.Results) song.RefreshThemeBrushes();
        foreach (var line in _lyrics) line.RefreshThemeBrushes();
        UpdatePlayingFavoriteUi();
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        var enabled = AutoStartToggle.IsOn;
        _services.Settings.AutoStartOnBoot = enabled;
        AutoStartHelper.SetAutoStart(enabled);
        _services.SaveSettings();
    }

    private void SilentAutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _services.Settings.SilentAutoStart = SilentAutoStartToggle.IsOn;
        _services.SaveSettings();
    }

    private void AutoPlayOnLaunchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _services.Settings.AutoPlayOnLaunch = AutoPlayOnLaunchToggle.IsOn;
        _services.SaveSettings();
    }

    private void CloseBehaviorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        var tag = (CloseBehaviorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "MinimizeToTray";
        _services.Settings.CloseBehavior = tag;
        _services.SaveSettings();
    }

    private async void AudioQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        var selectedQuality = (AudioQualityCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "2000kflac";
        _services.Settings.PreferredQuality = selectedQuality;
        _services.Coordinator.PreferredQuality = selectedQuality;
        _services.SaveSettings();
        UpdateAudioQualityUi(selectedQuality);

        if (_services.Coordinator.CurrentSong is not null && _services.Coordinator.Snapshot.HasMedia)
        {
            await _services.Coordinator.ReloadCurrentAsync(_lifetime.Token);
        }
    }

    private async void QualityFlyout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string quality) return;
        _services.Settings.PreferredQuality = quality;
        _services.Coordinator.PreferredQuality = quality;
        _services.SaveSettings();

        _applyingSettings = true;
        try
        {
            AudioQualityCombo.SelectedIndex = quality switch
            {
                "128kmp3" => 2,
                "320kmp3" => 1,
                _ => 0
            };
        }
        finally
        {
            _applyingSettings = false;
        }

        UpdateAudioQualityUi(quality);

        if (_services.Coordinator.CurrentSong is not null && _services.Coordinator.Snapshot.HasMedia)
        {
            await _services.Coordinator.ReloadCurrentAsync(_lifetime.Token);
        }
    }

    private void QualityFlyoutSettings_Click(object sender, RoutedEventArgs e)
    {
        _settingsHistory.Clear();
        if (_currentPage == "settings")
        {
            NavigateSettings("settings-playback", recordHistory: false, forceForward: true);
        }
        else
        {
            Navigate("settings-playback");
        }
    }

    private void NowPlayingQualityBadge_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();

        var flacItem = new RadioMenuFlyoutItem
        {
            Text = "无损品质 · FLAC",
            GroupName = "PlaybackQuality",
            Tag = "2000kflac",
            IsChecked = _services.Settings.PreferredQuality == "2000kflac"
        };
        flacItem.Click += QualityFlyout_Click;

        var highItem = new RadioMenuFlyoutItem
        {
            Text = "极高品质 · MP3",
            GroupName = "PlaybackQuality",
            Tag = "320kmp3",
            IsChecked = _services.Settings.PreferredQuality == "320kmp3"
        };
        highItem.Click += QualityFlyout_Click;

        var stdItem = new RadioMenuFlyoutItem
        {
            Text = "标准品质 · MP3",
            GroupName = "PlaybackQuality",
            Tag = "128kmp3",
            IsChecked = _services.Settings.PreferredQuality == "128kmp3"
        };
        stdItem.Click += QualityFlyout_Click;

        var settingsItem = new MenuFlyoutItem
        {
            Text = "音质与播放设置…",
            Icon = new FontIcon { Glyph = "\uE713" }
        };
        settingsItem.Click += QualityFlyoutSettings_Click;

        flyout.Items.Add(flacItem);
        flyout.Items.Add(highItem);
        flyout.Items.Add(stdItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(settingsItem);

        flyout.ShowAt(NowPlayingQualityBadge);
    }

    private void UpdateAudioQualityUi(string quality)
    {
    }

    private void AutoNavigateToNowPlaying_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _services.Settings.AutoNavigateToNowPlayingOnPlay = AutoNavigateToNowPlayingToggle.IsOn;
        _services.SaveSettings();
    }

    private void SkipUnplayable_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _services.Settings.AutoSkipUnplayable = SkipUnplayableToggle.IsOn;
        _services.Coordinator.SkipUnplayable = SkipUnplayableToggle.IsOn;
        _services.SaveSettings();
    }

    private void Retry_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _services.Settings.RetryOnceOnFailure = RetryToggle.IsOn;
        _services.Coordinator.RetryOnFailure = RetryToggle.IsOn;
        _services.SaveSettings();
    }

    private void ShowTranslation_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _services.Settings.ShowTranslation = ShowTranslationToggle.IsOn;
        _services.SaveSettings();
        UpdateTranslationButtonUi(ShowTranslationToggle.IsOn);
        // 翻译可见性由行模板决定
        ApplyLyrics(new LyricsChangedEventArgs(_services.Coordinator.Lyrics, _services.Coordinator.Snapshot.SongId, _services.Coordinator.Snapshot.Generation));
    }

    private void UseSystemMediaKeys_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _services.Settings.UseSystemMediaKeys = UseSystemMediaKeysToggle.IsOn;
        _services.SaveSettings();
        UpdateSystemMediaControls(_services.Coordinator.Snapshot);
    }

    private void AutoFollowLyrics_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _services.Settings.AutoFollowLyrics = AutoFollowLyricsToggle.IsOn;
        LyricFollowDelaySlider.IsEnabled = AutoFollowLyricsToggle.IsOn;
        _services.SaveSettings();
        _lyricAutoResumeTimer?.Stop();
        if (AutoFollowLyricsToggle.IsOn)
            ResumeLyricAutoFollow();
        else
        {
            _isManualLyricBrowsing = false;
            LyricsFollowStatusText.Text = "定位当前";
        }
    }

    private void LyricFollowDelay_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_applyingSettings) return;
        _services.Settings.LyricFollowDelaySeconds = Math.Clamp((int)Math.Round(e.NewValue), 2, 10);
        LyricFollowDelayText.Text = $"{_services.Settings.LyricFollowDelaySeconds} 秒";
        _services.SaveSettings();
    }

    private void ClickLyricToSeek_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings) return;
        _services.Settings.ClickLyricToSeek = ClickLyricToSeekToggle.IsOn;
        LyricsList.IsItemClickEnabled = ClickLyricToSeekToggle.IsOn;
        _services.SaveSettings();
    }
    private (string Page, FrameworkElement Panel, TranslateTransform Transform)[] GetSettingsPanelEntries() =>
    [
        ("settings", SettingsCategories, CategoriesTransform),
        ("settings-account", SettingsAccount, AccountTransform),
        ("settings-general", SettingsGeneral, GeneralTransform),
        ("settings-playback", SettingsPlayback, PlaybackTransform),
        ("settings-lyrics", SettingsLyrics, LyricsTransform),
        ("settings-main-lyrics", SettingsMainLyrics, MainLyricsTransform),
        ("settings-taskbar-lyrics", SettingsTaskbarLyrics, TaskbarLyricsTransform),
        ("settings-appearance", SettingsAppearance, AppearanceTransform),
        ("settings-storage", SettingsStorage, StorageTransform),
        ("settings-about", SettingsAbout, AboutTransform)
    ];

    private void CancelActiveSettingsTransition()
    {
        if (_settingsTransitionStoryboard is not null)
        {
            _settingsTransitionStoryboard.Stop();
            _settingsTransitionStoryboard = null;
        }

        if (_activeOutgoingPanel is not null)
        {
            _activeOutgoingPanel.Visibility = Visibility.Collapsed;
            _activeOutgoingPanel.Opacity = 1.0;
            _activeOutgoingPanel.IsHitTestVisible = true;
            if (_activeOutgoingTransform is not null)
            {
                _activeOutgoingTransform.X = 0;
            }
            _activeOutgoingPanel = null;
            _activeOutgoingTransform = null;
        }
    }

    private void SelectSettingsPageInstant(string page)
    {
        CancelActiveSettingsTransition();
        foreach (var (name, panel, transform) in GetSettingsPanelEntries())
        {
            if (name == page)
            {
                panel.Visibility = Visibility.Visible;
                panel.Opacity = 1.0;
                panel.IsHitTestVisible = true;
                transform.X = 0;
            }
            else
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 1.0;
                panel.IsHitTestVisible = true;
                transform.X = 0;
            }
        }
        _currentSettingsPage = page;
        MainScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);
        SyncNavigationSelection(Navigation.SettingsItem);
    }
    private void TransitionSettingsPage(string fromPage, string toPage, bool isForward)
    {
        if (fromPage == toPage) return;

        var entries = GetSettingsPanelEntries();
        var fromEntry = entries.FirstOrDefault(e => e.Page == fromPage);
        var toEntry = entries.FirstOrDefault(e => e.Page == toPage);

        if (toEntry.Panel is null) return;

        var uiSettings = new Windows.UI.ViewManagement.UISettings();
        if (!uiSettings.AnimationsEnabled || fromEntry.Panel is null || fromEntry.Panel.Visibility != Visibility.Visible)
        {
            SelectSettingsPageInstant(toPage);
            return;
        }

        CancelActiveSettingsTransition();

        var outgoingPanel = fromEntry.Panel;
        var outgoingTransform = fromEntry.Transform;
        var incomingPanel = toEntry.Panel;
        var incomingTransform = toEntry.Transform;

        _activeOutgoingPanel = outgoingPanel;
        _activeOutgoingTransform = outgoingTransform;

        foreach (var (name, panel, transform) in entries)
        {
            if (panel != outgoingPanel && panel != incomingPanel)
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 1.0;
                panel.IsHitTestVisible = true;
                transform.X = 0;
            }
        }

        double incomingStartX = isForward ? 72.0 : -48.0;
        double outgoingEndX = isForward ? -48.0 : 72.0;

        incomingPanel.Visibility = Visibility.Visible;
        incomingPanel.IsHitTestVisible = true;
        incomingPanel.Opacity = 0.0;
        incomingTransform.X = incomingStartX;

        outgoingPanel.IsHitTestVisible = false;
        outgoingPanel.Opacity = 1.0;
        outgoingTransform.X = 0.0;

        MainScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();

        var inX = new DoubleAnimation
        {
            From = incomingStartX,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = easeOut,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(inX, incomingTransform);
        Storyboard.SetTargetProperty(inX, "X");
        storyboard.Children.Add(inX);

        var inFade = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = easeOut,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(inFade, incomingPanel);
        Storyboard.SetTargetProperty(inFade, "Opacity");
        storyboard.Children.Add(inFade);

        var outX = new DoubleAnimation
        {
            From = 0.0,
            To = outgoingEndX,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = easeOut,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(outX, outgoingTransform);
        Storyboard.SetTargetProperty(outX, "X");
        storyboard.Children.Add(outX);

        var outFade = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = easeOut,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(outFade, outgoingPanel);
        Storyboard.SetTargetProperty(outFade, "Opacity");
        storyboard.Children.Add(outFade);

        storyboard.Completed += (_, _) =>
        {
            storyboard.Stop();
            outgoingPanel.Visibility = Visibility.Collapsed;
            outgoingPanel.Opacity = 1.0;
            outgoingPanel.IsHitTestVisible = true;
            outgoingTransform.X = 0;

            incomingPanel.Opacity = 1.0;
            incomingTransform.X = 0;

            if (_activeOutgoingPanel == outgoingPanel)
            {
                _activeOutgoingPanel = null;
                _activeOutgoingTransform = null;
            }
            _settingsTransitionStoryboard = null;
        };

        _currentSettingsPage = toPage;
        SyncNavigationSelection(Navigation.SettingsItem);

        _settingsTransitionStoryboard = storyboard;
        storyboard.Begin();
    }

    private async void ChangeDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            var chosen = folder.Path;
            if (string.Equals(Path.GetFullPath(chosen), Path.GetFullPath(_services.Paths.Root), StringComparison.OrdinalIgnoreCase))
                return;

            _services.SwitchDataRoot(chosen);
            UpdateStorageUi();
            await ShowDialogAsync("数据文件夹", $"下次启动将迁移数据至：\n{chosen}\n\n请从托盘完全退出程序后重新打开。当前仍使用原目录；迁移会保留原文件。");
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("提示", $"更改数据文件夹失败：{exception.Message}");
        }
    }

    private async void ResetDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaultRoot = AppPaths.GetStandardDefaultRoot();
            if (string.Equals(Path.GetFullPath(defaultRoot), Path.GetFullPath(_services.Paths.Root), StringComparison.OrdinalIgnoreCase))
                return;

            _services.SwitchDataRoot(defaultRoot);
            UpdateStorageUi();
            await ShowDialogAsync("数据文件夹", $"已安排下次启动恢复默认位置（请完全退出后重新打开）：\n{defaultRoot}");
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("提示", $"恢复默认数据文件夹失败：{exception.Message}");
        }
    }

    private async void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!System.IO.Directory.Exists(_services.Paths.Root))
            {
                System.IO.Directory.CreateDirectory(_services.Paths.Root);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_services.Paths.Root) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("提示", $"无法打开文件夹：{exception.Message}");
        }
    }

    private async void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_services.Paths.LogPath);
            if (!string.IsNullOrEmpty(directory) && System.IO.Directory.Exists(directory))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            else
                await ShowDialogAsync("提示", "日志目录尚未生成，先运行一次播放或登录。");
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("提示", $"无法打开日志目录：{exception.Message}");
        }
    }

    private void UpdateStorageUi()
    {
        DataFolderText.Text = _services.Paths.Root;
        ResetDataFolderButton.Visibility = _services.Paths.IsCustomRoot ? Visibility.Visible : Visibility.Collapsed;

        _applyingSettings = true;
        try
        {
            if (AudioCacheQuotaCombo.Items.Count == 0)
            {
                AudioCacheQuotaCombo.Items.Add(new ComboBoxItem { Content = "500 MB", Tag = 500 });
                AudioCacheQuotaCombo.Items.Add(new ComboBoxItem { Content = "1 GB", Tag = 1024 });
                AudioCacheQuotaCombo.Items.Add(new ComboBoxItem { Content = "2 GB", Tag = 2048 });
                AudioCacheQuotaCombo.Items.Add(new ComboBoxItem { Content = "5 GB", Tag = 5120 });
                AudioCacheQuotaCombo.Items.Add(new ComboBoxItem { Content = "无限制", Tag = 0 });
            }

            var limit = _services.Settings.AudioCacheLimitMb;
            var matchedIndex = -1;
            for (var i = 0; i < AudioCacheQuotaCombo.Items.Count; i++)
            {
                if (AudioCacheQuotaCombo.Items[i] is ComboBoxItem item && item.Tag is int tag && tag == limit)
                {
                    matchedIndex = i;
                    break;
                }
            }
            AudioCacheQuotaCombo.SelectedIndex = matchedIndex >= 0 ? matchedIndex : 1;
        }
        finally
        {
            _applyingSettings = false;
        }

        _ = RefreshAudioCacheSizeUiAsync();
    }

    private async Task RefreshAudioCacheSizeUiAsync()
    {
        try
        {
            var sizeInfo = await _services.AudioCache.GetCacheSizeAsync();
            DispatcherQueue.TryEnqueue(() =>
            {
                AudioCacheSizeDescription.Text = sizeInfo.SummaryText;
            });
        }
        catch
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AudioCacheSizeDescription.Text = "获取缓存大小失败";
            });
        }
    }

    private void AudioCacheQuotaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings) return;
        if (AudioCacheQuotaCombo.SelectedItem is ComboBoxItem item && item.Tag is int limit)
        {
            _services.Settings.AudioCacheLimitMb = limit;
            _services.Coordinator.AudioCacheLimitMb = limit;
            _services.SaveSettings();
            _ = Task.Run(async () =>
            {
                await _services.AudioCache.PruneIfNeededAsync(limit);
                await RefreshAudioCacheSizeUiAsync();
            });
        }
    }

    private async void OpenAudioCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = _services.AudioCache.CacheDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("提示", $"无法打开缓存目录：{ex.Message}");
        }
    }

    private async void ClearAudioCache_Click(object sender, RoutedEventArgs e)
    {
        ClearAudioCacheButton.IsEnabled = false;
        try
        {
            await _services.AudioCache.ClearCacheAsync();
            await RefreshAudioCacheSizeUiAsync();
        }
        finally
        {
            ClearAudioCacheButton.IsEnabled = true;
        }
    }

}
