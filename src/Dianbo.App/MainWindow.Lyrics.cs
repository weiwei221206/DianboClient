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
    private readonly ObservableCollection<LyricLineViewModel> _lyrics = [];
    private Dianbo.Core.Lyrics.LyricDocument _displayLyricDocument = Dianbo.Core.Lyrics.LyricDocument.Empty;
    private int _lastRenderedLyricIndex = -2;
    private bool _isLyricsSubPageActive;
    private bool _isPageSwitching;
    private Storyboard? _subPageTransitionStoryboard;
    private long _lastWheelSwitchTicks;
    private DispatcherTimer? _lyricAutoResumeTimer;
    private bool _isManualLyricBrowsing;
    private int _lyricAutoResumeCountdown = 4;
    private ScrollViewer? _lyricsScrollViewer;
    private bool _lyricWheelHandlerAttached;
    private bool _isProgrammaticLyricScrolling;
    private int _lyricScrollRequest;
    private double? _programmaticLyricTargetOffset;

    private void TranslationToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = TranslationToggle.IsChecked == true;
        _services.Settings.ShowTranslation = enabled;
        _applyingSettings = true;
        try
        {
            ShowTranslationToggle.IsOn = enabled;
        }
        finally
        {
            _applyingSettings = false;
        }
        _services.SaveSettings();
        ApplyLyrics(new LyricsChangedEventArgs(_services.Coordinator.Lyrics, _services.Coordinator.Snapshot.SongId, _services.Coordinator.Snapshot.Generation));
        UpdateTranslationButtonUi(enabled);
    }

    private void UpdateTranslationButtonUi(bool enabled)
    {
        if (TranslationToggle is null) return;
        TranslationToggle.IsChecked = enabled;
        ToolTipService.SetToolTip(TranslationToggle, enabled ? "已显示翻译歌词（点击隐藏）" : "显示翻译歌词（点击开启）");
    }

    private void LyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_isLyricsSubPageActive)
        {
            SwitchToLyricsSubPage(animated: true);
        }
        else
        {
            ResumeLyricAutoFollow();
        }
    }



    private void ApplyLyrics(LyricsChangedEventArgs args)
    {
        _lyricScrollRequest++;
        _isProgrammaticLyricScrolling = false;
        _programmaticLyricTargetOffset = null;
        _lastRenderedLyricIndex = -2;
        _lyrics.Clear();
        _lyricAutoResumeTimer?.Stop();
        _isManualLyricBrowsing = false;
        if (LyricsFollowStatusText is not null)
        {
            LyricsFollowStatusText.Text = _services.Settings.AutoFollowLyrics ? "跟随中" : "定位当前";
        }
  
        _displayLyricDocument = args.Document;

        var index = 0;
        foreach (var group in _displayLyricDocument.Groups)
        {
            _lyrics.Add(new LyricLineViewModel(index++, group, null)
            {
                ShowTranslation = _services.Settings.ShowTranslation
            });
        }
        foreach (var unpaired in _displayLyricDocument.UnpairedLines)
        {
            _lyrics.Add(new LyricLineViewModel(index++, null, unpaired)
            {
                ShowTranslation = _services.Settings.ShowTranslation
            });
        }
        UpdateLyricsVisibility(_services.Coordinator.CurrentSong is not null);
        UpdateCurrentLyric(_services.Coordinator.Snapshot.Position);
    }

    private void UpdateLyricsVisibility(bool hasSong)
    {
        if (!hasSong)
        {
            LyricsList.Visibility = Visibility.Collapsed;
            LyricsEmptyPanel.Visibility = Visibility.Visible;
            LyricsEmptyTitle.Text = "暂无播放中的音乐";
            LyricsEmptySubtitle.Text = "从发现音乐或搜索中挑选一首歌开始聆听";
        }
        else if (_lyrics.Count == 0)
        {
            LyricsList.Visibility = Visibility.Collapsed;
            LyricsEmptyPanel.Visibility = Visibility.Visible;
            LyricsEmptyTitle.Text = "暂无歌词，静静聆听";
            LyricsEmptySubtitle.Text = "当前歌曲暂无同步歌词";
        }
        else
        {
            LyricsList.Visibility = Visibility.Visible;
            LyricsEmptyPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCurrentLyric(TimeSpan position)
    {
        if (_lyrics.Count == 0) return;
        var document = _displayLyricDocument;
        var target = document.IndexAt(position);
        if (target == _lastRenderedLyricIndex) return;
        _lastRenderedLyricIndex = target;
        for (var i = 0; i < _lyrics.Count; i++) _lyrics[i].IsCurrent = i == target;
        if (_isLyricsSubPageActive && _services.Settings.AutoFollowLyrics
            && !_isManualLyricBrowsing && target >= 0 && target < _lyrics.Count)
        {
            ScrollToCurrentLyric();
        }
    }

    private void LyricsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (!_services.Settings.ClickLyricToSeek
            || e.ClickedItem is not LyricLineViewModel line || line.Group is null) return;
        _ = _services.Coordinator.SeekAsync(TimeSpan.FromMilliseconds(line.StartTimeMs), _lifetime.Token);
        ResumeLyricAutoFollow();
    }

    private void LyricRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LyricLineViewModel line })
            line.IsHovered = true;
    }

    private void LyricRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LyricLineViewModel line })
            line.IsHovered = false;
    }

    private void ScrollToCurrentLyric()
    {
        if (_lyrics.Count == 0) return;
        var target = _lastRenderedLyricIndex;
        if (target < 0 || target >= _lyrics.Count) return;

        var request = ++_lyricScrollRequest;
        _isProgrammaticLyricScrolling = true;
        _programmaticLyricTargetOffset = null;
        if (LyricsList.ContainerFromIndex(target) is null)
            LyricsList.ScrollIntoView(_lyrics[target], ScrollIntoViewAlignment.Default);
        _dispatcher.Post(() =>
        {
            if (request != _lyricScrollRequest || _isManualLyricBrowsing
                || !_isLyricsSubPageActive) return;
            var scrollViewer = _lyricsScrollViewer ??= FindVisualChild<ScrollViewer>(LyricsList);
            LyricsList.UpdateLayout();
            if (scrollViewer is null || LyricsList.ContainerFromIndex(target) is not FrameworkElement container)
            {
                _isProgrammaticLyricScrolling = false;
                _programmaticLyricTargetOffset = null;
                return;
            }

            var top = container.TransformToVisual(scrollViewer)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            var centeredOffset = scrollViewer.VerticalOffset + top
                + (container.ActualHeight - scrollViewer.ViewportHeight) / 2;
            BeginLyricScrollAnimation(scrollViewer,
                Math.Clamp(centeredOffset, 0, scrollViewer.ScrollableHeight));
        });
    }

    private void BeginLyricScrollAnimation(ScrollViewer scrollViewer, double targetOffset)
    {
        if (Math.Abs(targetOffset - scrollViewer.VerticalOffset) < 1)
        {
            _isProgrammaticLyricScrolling = false;
            _programmaticLyricTargetOffset = null;
            return;
        }

        _programmaticLyricTargetOffset = targetOffset;
        if (!scrollViewer.ChangeView(null, targetOffset, null, disableAnimation: false))
        {
            _isProgrammaticLyricScrolling = false;
            _programmaticLyricTargetOffset = null;
        }
    }

    private void OnUserBrowsingLyrics()
    {
        if (_lyrics.Count == 0) return;
        _lyricScrollRequest++;
        if (_isProgrammaticLyricScrolling && _lyricsScrollViewer is not null)
            _lyricsScrollViewer.ChangeView(null, _lyricsScrollViewer.VerticalOffset, null, disableAnimation: true);
        _isProgrammaticLyricScrolling = false;
        _programmaticLyricTargetOffset = null;
        _isManualLyricBrowsing = true;
        _lyricAutoResumeCountdown = Math.Clamp(_services.Settings.LyricFollowDelaySeconds, 2, 10);
        if (!_services.Settings.AutoFollowLyrics)
        {
            _lyricAutoResumeTimer?.Stop();
            LyricsFollowStatusText.Text = "定位当前";
            return;
        }
        LyricsFollowStatusText.Text = "恢复跟随";

        if (_lyricAutoResumeTimer is null)
        {
            _lyricAutoResumeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _lyricAutoResumeTimer.Tick += LyricAutoResumeTimer_Tick;
        }
        _lyricAutoResumeTimer.Stop();
        _lyricAutoResumeTimer.Start();
    }

    private void LyricAutoResumeTimer_Tick(object? sender, object e)
    {
        _lyricAutoResumeCountdown--;
        if (_lyricAutoResumeCountdown <= 0)
        {
            ResumeLyricAutoFollow();
        }
    }

    private void ResumeLyricAutoFollow()
    {
        _lyricAutoResumeTimer?.Stop();
        _isManualLyricBrowsing = false;
        LyricsFollowStatusText.Text = _services.Settings.AutoFollowLyrics ? "跟随中" : "定位当前";
        ScrollToCurrentLyric();
    }

    private void LyricsScrollArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLyricsPadding();
        if (_isLyricsSubPageActive && _services.Settings.AutoFollowLyrics
            && !_isManualLyricBrowsing && _lastRenderedLyricIndex >= 0)
            ScrollToCurrentLyric();
    }

    private void UpdateLyricsPadding()
    {
        if (_currentPage != "playing" || PlayingPage is null || PlayingPage.Visibility != Visibility.Visible)
        {
            return;
        }

        double viewportHeight = LyricsScrollArea.ActualHeight;

        if (viewportHeight <= 0) return;

        double halfH = viewportHeight / 2.0;

        var current = LyricsList.Padding;
        if (Math.Abs(current.Top - halfH) < 1.0 && Math.Abs(current.Bottom - halfH) < 1.0)
        {
            return;
        }

        LyricsList.Padding = new Thickness(16, halfH, 16, halfH);
    }

    private bool IsPlayingLandscape(double width = 0, double height = 0)
    {
        if (width <= 0 && PlayingPage is not null) width = PlayingPage.ActualWidth;
        if (height <= 0 && PlayingPage is not null) height = PlayingPage.ActualHeight;
        return width >= 660 && width >= height * 0.95;
    }

    private void CancelActiveSubPageTransition()
    {
        if (_subPageTransitionStoryboard is not null)
        {
            try
            {
                _subPageTransitionStoryboard.Stop();
            }
            catch
            {
            }
            _subPageTransitionStoryboard = null;
        }

        _isPageSwitching = false;

        if (CoverPageTransform is not null)
        {
            CoverPageTransform.X = 0;
            CoverPageTransform.Y = 0;
        }
        if (LyricsPageTransform is not null)
        {
            LyricsPageTransform.X = 0;
            LyricsPageTransform.Y = 0;
        }

        if (_isLyricsSubPageActive)
        {
            if (PlayingCoverPage is not null)
            {
                PlayingCoverPage.Visibility = Visibility.Collapsed;
                PlayingCoverPage.Opacity = 1;
            }
            if (PlayingLyricsPage is not null)
            {
                PlayingLyricsPage.Visibility = Visibility.Visible;
                PlayingLyricsPage.Opacity = 1;
            }
        }
        else
        {
            if (PlayingLyricsPage is not null)
            {
                PlayingLyricsPage.Visibility = Visibility.Collapsed;
                PlayingLyricsPage.Opacity = 1;
            }
            if (PlayingCoverPage is not null)
            {
                PlayingCoverPage.Visibility = Visibility.Visible;
                PlayingCoverPage.Opacity = 1;
            }
        }
    }

    private void UpdatePlayingLayout(double width, double height)
    {
        if (width <= 0 || height <= 0) return;

        bool isLandscape = IsPlayingLandscape(width, height);

        if (isLandscape)
        {
            CoverLayoutCol0.Width = new GridLength(1.0, GridUnitType.Star);
            CoverLayoutCol1.Width = new GridLength(1.0, GridUnitType.Star);
            CoverLayoutRow0.Height = new GridLength(1.0, GridUnitType.Star);
            CoverLayoutRow1.Height = new GridLength(0);

            Grid.SetColumn(CoverSectionGrid, 0);
            Grid.SetRow(CoverSectionGrid, 0);
            Grid.SetColumn(InfoAndControlsGrid, 1);
            Grid.SetRow(InfoAndControlsGrid, 0);

            CoverToLyricsIcon.Glyph = "\uE70D";
            CoverToLyricsText.Text = "向下滚动查看歌词";
            LyricsBackToCoverIcon.Glyph = "\uE70E";
            LyricsBackToCoverText.Text = "返回封面";

            var targetMargin = new Thickness(16, 10, 16, 14);
            if (LyricsCardBorder.Margin != targetMargin)
            {
                LyricsCardBorder.Margin = targetMargin;
            }
        }
        else
        {
            CoverLayoutCol0.Width = new GridLength(1.0, GridUnitType.Star);
            CoverLayoutCol1.Width = new GridLength(0);
            CoverLayoutRow0.Height = new GridLength(1.0, GridUnitType.Star);
            CoverLayoutRow1.Height = new GridLength(1.0, GridUnitType.Star);

            Grid.SetColumn(CoverSectionGrid, 0);
            Grid.SetRow(CoverSectionGrid, 0);
            Grid.SetColumn(InfoAndControlsGrid, 0);
            Grid.SetRow(InfoAndControlsGrid, 1);

            CoverToLyricsIcon.Glyph = "\uE76C";
            CoverToLyricsText.Text = "向右翻页查看歌词";
            LyricsBackToCoverIcon.Glyph = "\uE76B";
            LyricsBackToCoverText.Text = "返回封面";

            var targetMargin = new Thickness(8, 6, 8, 8);
            if (LyricsCardBorder.Margin != targetMargin)
            {
                LyricsCardBorder.Margin = targetMargin;
            }
        }

        UpdateLyricsPadding();
    }

    private void CoverToLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        SwitchToLyricsSubPage(animated: true);
    }

    private void LyricsBackToCoverButton_Click(object sender, RoutedEventArgs e)
    {
        SwitchToCoverSubPage(animated: true);
    }

    private void SwitchToLyricsSubPage(bool animated = true)
    {
        if (_isLyricsSubPageActive) return;
        CancelActiveSubPageTransition();
        _isLyricsSubPageActive = true;
        ResumeLyricAutoFollow();

        var uiSettings = new Windows.UI.ViewManagement.UISettings();
        if (!animated || !uiSettings.AnimationsEnabled)
        {
            PlayingCoverPage.Visibility = Visibility.Collapsed;
            CoverPageTransform.X = 0;
            CoverPageTransform.Y = 0;
            PlayingCoverPage.Opacity = 1;

            PlayingLyricsPage.Visibility = Visibility.Visible;
            LyricsPageTransform.X = 0;
            LyricsPageTransform.Y = 0;
            PlayingLyricsPage.Opacity = 1;
            ScrollToCurrentLyric();
            return;
        }

        _isPageSwitching = true;

        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(320);

        bool isLandscape = IsPlayingLandscape();
        PlayingLyricsPage.Visibility = Visibility.Visible;

        if (isLandscape)
        {
            CoverPageTransform.X = 0;
            LyricsPageTransform.X = 0;

            var animOutY = new DoubleAnimation
            {
                From = 0,
                To = -Math.Max(120, PlayingPage.ActualHeight * 0.45),
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(animOutY, CoverPageTransform);
            Storyboard.SetTargetProperty(animOutY, "Y");
            sb.Children.Add(animOutY);

            var animOutOpacity = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            Storyboard.SetTarget(animOutOpacity, PlayingCoverPage);
            Storyboard.SetTargetProperty(animOutOpacity, "Opacity");
            sb.Children.Add(animOutOpacity);

            var animInY = new DoubleAnimation
            {
                From = Math.Max(200, PlayingPage.ActualHeight * 0.8),
                To = 0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(animInY, LyricsPageTransform);
            Storyboard.SetTargetProperty(animInY, "Y");
            sb.Children.Add(animInY);

            var animInOpacity = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = duration
            };
            Storyboard.SetTarget(animInOpacity, PlayingLyricsPage);
            Storyboard.SetTargetProperty(animInOpacity, "Opacity");
            sb.Children.Add(animInOpacity);
        }
        else
        {
            CoverPageTransform.Y = 0;
            LyricsPageTransform.Y = 0;

            var animOutX = new DoubleAnimation
            {
                From = 0,
                To = -Math.Max(120, PlayingPage.ActualWidth * 0.45),
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(animOutX, CoverPageTransform);
            Storyboard.SetTargetProperty(animOutX, "X");
            sb.Children.Add(animOutX);

            var animOutOpacity = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            Storyboard.SetTarget(animOutOpacity, PlayingCoverPage);
            Storyboard.SetTargetProperty(animOutOpacity, "Opacity");
            sb.Children.Add(animOutOpacity);

            var animInX = new DoubleAnimation
            {
                From = Math.Max(200, PlayingPage.ActualWidth * 0.8),
                To = 0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(animInX, LyricsPageTransform);
            Storyboard.SetTargetProperty(animInX, "X");
            sb.Children.Add(animInX);

            var animInOpacity = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = duration
            };
            Storyboard.SetTarget(animInOpacity, PlayingLyricsPage);
            Storyboard.SetTargetProperty(animInOpacity, "Opacity");
            sb.Children.Add(animInOpacity);
        }

        sb.Completed += (_, _) =>
        {
            sb.Stop();
            _isPageSwitching = false;
            _subPageTransitionStoryboard = null;
            PlayingCoverPage.Visibility = Visibility.Collapsed;
            CoverPageTransform.X = 0;
            CoverPageTransform.Y = 0;
            PlayingCoverPage.Opacity = 1;
            LyricsPageTransform.X = 0;
            LyricsPageTransform.Y = 0;
            PlayingLyricsPage.Opacity = 1;
            ScrollToCurrentLyric();
        };

        _subPageTransitionStoryboard = sb;
        sb.Begin();
    }

    private void SwitchToCoverSubPage(bool animated = true)
    {
        if (!_isLyricsSubPageActive) return;
        CancelActiveSubPageTransition();
        _isLyricsSubPageActive = false;

        var uiSettings = new Windows.UI.ViewManagement.UISettings();
        if (!animated || !uiSettings.AnimationsEnabled)
        {
            PlayingLyricsPage.Visibility = Visibility.Collapsed;
            LyricsPageTransform.X = 0;
            LyricsPageTransform.Y = 0;
            PlayingLyricsPage.Opacity = 1;

            PlayingCoverPage.Visibility = Visibility.Visible;
            CoverPageTransform.X = 0;
            CoverPageTransform.Y = 0;
            PlayingCoverPage.Opacity = 1;
            return;
        }

        _isPageSwitching = true;

        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(320);

        bool isLandscape = IsPlayingLandscape();
        PlayingCoverPage.Visibility = Visibility.Visible;

        if (isLandscape)
        {
            CoverPageTransform.X = 0;
            LyricsPageTransform.X = 0;

            var animOutY = new DoubleAnimation
            {
                From = 0,
                To = Math.Max(200, PlayingPage.ActualHeight * 0.8),
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(animOutY, LyricsPageTransform);
            Storyboard.SetTargetProperty(animOutY, "Y");
            sb.Children.Add(animOutY);

            var animOutOpacity = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            Storyboard.SetTarget(animOutOpacity, PlayingLyricsPage);
            Storyboard.SetTargetProperty(animOutOpacity, "Opacity");
            sb.Children.Add(animOutOpacity);

            var animInY = new DoubleAnimation
            {
                From = -Math.Max(120, PlayingPage.ActualHeight * 0.45),
                To = 0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(animInY, CoverPageTransform);
            Storyboard.SetTargetProperty(animInY, "Y");
            sb.Children.Add(animInY);

            var animInOpacity = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = duration
            };
            Storyboard.SetTarget(animInOpacity, PlayingCoverPage);
            Storyboard.SetTargetProperty(animInOpacity, "Opacity");
            sb.Children.Add(animInOpacity);
        }
        else
        {
            CoverPageTransform.Y = 0;
            LyricsPageTransform.Y = 0;

            var animOutX = new DoubleAnimation
            {
                From = 0,
                To = Math.Max(200, PlayingPage.ActualWidth * 0.8),
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(animOutX, LyricsPageTransform);
            Storyboard.SetTargetProperty(animOutX, "X");
            sb.Children.Add(animOutX);

            var animOutOpacity = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            Storyboard.SetTarget(animOutOpacity, PlayingLyricsPage);
            Storyboard.SetTargetProperty(animOutOpacity, "Opacity");
            sb.Children.Add(animOutOpacity);

            var animInX = new DoubleAnimation
            {
                From = -Math.Max(120, PlayingPage.ActualWidth * 0.45),
                To = 0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(animInX, CoverPageTransform);
            Storyboard.SetTargetProperty(animInX, "X");
            sb.Children.Add(animInX);

            var animInOpacity = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = duration
            };
            Storyboard.SetTarget(animInOpacity, PlayingCoverPage);
            Storyboard.SetTargetProperty(animInOpacity, "Opacity");
            sb.Children.Add(animInOpacity);
        }

        sb.Completed += (_, _) =>
        {
            sb.Stop();
            _isPageSwitching = false;
            _subPageTransitionStoryboard = null;
            PlayingLyricsPage.Visibility = Visibility.Collapsed;
            LyricsPageTransform.X = 0;
            LyricsPageTransform.Y = 0;
            PlayingLyricsPage.Opacity = 1;
            CoverPageTransform.X = 0;
            CoverPageTransform.Y = 0;
            PlayingCoverPage.Opacity = 1;
        };

        _subPageTransitionStoryboard = sb;
        sb.Begin();
    }

    private void PlayingPage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_currentPage != "playing") return;
        var props = e.GetCurrentPoint(PlayingPage).Properties;
        int delta = props.MouseWheelDelta;
        if (delta == 0 || _isPageSwitching) return;

        var now = DateTime.UtcNow.Ticks;
        if (now - _lastWheelSwitchTicks < TimeSpan.FromMilliseconds(260).Ticks)
        {
            return;
        }

        if (!_isLyricsSubPageActive)
        {
            if (delta < 0)
            {
                _lastWheelSwitchTicks = now;
                SwitchToLyricsSubPage(animated: true);
                e.Handled = true;
            }
        }
        else
        {
            if (delta > 0)
            {
                _lastWheelSwitchTicks = now;
                SwitchToCoverSubPage(animated: true);
                e.Handled = true;
            }
        }
    }

    private void LyricsList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_currentPage != "playing") return;
        var props = e.GetCurrentPoint(LyricsList).Properties;
        int delta = props.MouseWheelDelta;
        if (delta == 0 || _isPageSwitching) return;

        var sv = _lyricsScrollViewer ??= FindVisualChild<ScrollViewer>(LyricsList);

        if (delta > 0 && (sv is null || sv.VerticalOffset <= 2 || _lyrics.Count == 0))
        {
            var now = DateTime.UtcNow.Ticks;
            if (now - _lastWheelSwitchTicks >= TimeSpan.FromMilliseconds(260).Ticks)
            {
                _lastWheelSwitchTicks = now;
                SwitchToCoverSubPage(animated: true);
                e.Handled = true;
                return;
            }
        }

        OnUserBrowsingLyrics();
    }

    private void LyricsList_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_lyricWheelHandlerAttached)
        {
            LyricsList.AddHandler(UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(LyricsList_PointerWheelChanged), true);
            _lyricWheelHandlerAttached = true;
        }
        _lyricsScrollViewer = FindVisualChild<ScrollViewer>(LyricsList);
        if (_lyricsScrollViewer is not null)
        {
            _lyricsScrollViewer.ViewChanged += LyricsScrollViewer_ViewChanged;
        }
        UpdateLyricsPadding();
        if (_isLyricsSubPageActive && !_isManualLyricBrowsing
            && _lastRenderedLyricIndex >= 0)
            ScrollToCurrentLyric();
    }

    private void LyricsScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isProgrammaticLyricScrolling)
        {
            if (!e.IsIntermediate && sender is ScrollViewer scrollViewer
                && _programmaticLyricTargetOffset is double target
                && Math.Abs(scrollViewer.VerticalOffset - target) < 2)
            {
                _isProgrammaticLyricScrolling = false;
                _programmaticLyricTargetOffset = null;
            }
            return;
        }
        if (_currentPage != "playing") return;
        if (e.IsIntermediate)
        {
            OnUserBrowsingLyrics();
        }
    }

    private void OnNavigatedToPlaying()
    {
        CancelActiveSubPageTransition();
        UpdatePlayingLayout(PlayingPage.ActualWidth, PlayingPage.ActualHeight);
        if (_isLyricsSubPageActive)
        {
            ResumeLyricAutoFollow();
        }
    }

    private void OnNavigatedAwayFromPlaying()
    {
        _lyricScrollRequest++;
        _isProgrammaticLyricScrolling = false;
        _programmaticLyricTargetOffset = null;
        CancelActiveSubPageTransition();
        _lyricAutoResumeTimer?.Stop();
        _isManualLyricBrowsing = false;
        if (LyricsFollowStatusText is not null)
        {
            LyricsFollowStatusText.Text = _services.Settings.AutoFollowLyrics ? "跟随中" : "定位当前";
        }
    }

    private void PlayingPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0 || e.NewSize.Width <= 0) return;
        CancelActiveSubPageTransition();
        UpdatePlayingLayout(e.NewSize.Width, e.NewSize.Height);
    }

}
