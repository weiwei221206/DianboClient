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
    private string _currentLibraryPage = "library";
    private Storyboard? _libraryTransitionStoryboard;
    private FrameworkElement? _activeLibraryOutgoingPanel;
    private TranslateTransform? _activeLibraryOutgoingTransform;
    private FrameworkElement? _activeLibraryIncomingPanel;
    private TranslateTransform? _activeLibraryIncomingTransform;
    private string? _pendingLibraryTargetPage;
    private CancellationTokenSource? _libResultPillCts;
    private Storyboard? _libResultPillStoryboard;
    private readonly ObservableCollection<SongViewModel> _fondSongs = [];
    private readonly ObservableCollection<SongViewModel> _recentSongs = [];
    private Playlist? _currentFondPlaylist;
    private bool _libraryNavUserExpanded = true;
    private bool _isRestoringExpandedState;
    private FrameworkElement? _hookedLibraryContentGrid;
    private readonly PointerEventHandler _libraryPointerReleasedHandler;
    private readonly PointerEventHandler _libraryPointerPressedHandler;
    private bool _lastPointerDownOnChevron;

    private (string Page, FrameworkElement Panel, TranslateTransform Transform)[] GetLibraryPanelEntries() =>
    [
        ("library", LibraryRootPanel, LibRootTransform),
        ("library:fond", LibraryFondPanel, LibFondTransform),
        ("library:recent", LibraryRecentPanel, LibRecentTransform),
        ("library:playlist", LibraryPlaylistPanel, LibPlaylistTransform)
    ];

    private static string NormalizeLibraryPage(string page) =>
        page.StartsWith("library:playlist:") ? "library:playlist" : page;

    private static bool IsSameLibraryPanel(string pageA, string pageB) =>
        NormalizeLibraryPage(pageA) == NormalizeLibraryPage(pageB);

    private int GetLibraryPageNavOrder(string page)
    {
        if (page == "library") return 0;
        if (page == "library:fond") return 1;
        if (page == "library:recent") return 2;
        if (page.StartsWith("library:playlist:"))
        {
            var idStr = page["library:playlist:".Length..];
            if (long.TryParse(idStr, out var id))
            {
                for (int i = 0; i < _userPlaylists.Count; i++)
                {
                    if (_userPlaylists[i].Id == id) return 3 + i;
                }
            }
            return 3;
        }
        return 0;
    }

    private bool DetermineLibraryIsForward(string fromPage, string toPage)
    {
        if (toPage == "library") return false;
        if (fromPage == "library") return true;
        return GetLibraryPageNavOrder(toPage) >= GetLibraryPageNavOrder(fromPage);
    }

    private void CancelActiveLibraryTransition()
    {
        DismissLibResultPillInstant();
        _pendingLibraryTargetPage = null;
        if (_libraryTransitionStoryboard is not null)
        {
            _libraryTransitionStoryboard.Stop();
            _libraryTransitionStoryboard = null;
        }

        if (_activeLibraryOutgoingPanel is not null)
        {
            if (!ReferenceEquals(_activeLibraryOutgoingPanel, _activeLibraryIncomingPanel))
            {
                _activeLibraryOutgoingPanel.Visibility = Visibility.Collapsed;
            }
            _activeLibraryOutgoingPanel.Opacity = 1.0;
            _activeLibraryOutgoingPanel.IsHitTestVisible = true;
            if (_activeLibraryOutgoingTransform is not null)
            {
                _activeLibraryOutgoingTransform.X = 0;
            }
            _activeLibraryOutgoingPanel = null;
            _activeLibraryOutgoingTransform = null;
        }

        if (_activeLibraryIncomingPanel is not null)
        {
            _activeLibraryIncomingPanel.Opacity = 1.0;
            _activeLibraryIncomingPanel.IsHitTestVisible = true;
            if (_activeLibraryIncomingTransform is not null)
            {
                _activeLibraryIncomingTransform.X = 0;
            }
            _activeLibraryIncomingPanel = null;
            _activeLibraryIncomingTransform = null;
        }
    }

    private void SelectLibraryPageInstant(string page)
    {
        CancelActiveLibraryTransition();
        var normalized = NormalizeLibraryPage(page);
        foreach (var (name, panel, transform) in GetLibraryPanelEntries())
        {
            if (name == normalized)
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
        _currentLibraryPage = page;
        MainScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);
    }

    private void TransitionLibraryPage(string fromPage, string toPage, bool isForward, Action? onSwapContent = null)
    {
        if (fromPage == toPage) return;

        var normFrom = NormalizeLibraryPage(fromPage);
        var normTo = NormalizeLibraryPage(toPage);
        if (normFrom == normTo && !toPage.StartsWith("library:playlist:")) return;

        var entries = GetLibraryPanelEntries();
        var fromEntry = entries.FirstOrDefault(e => e.Page == normFrom);
        var toEntry = entries.FirstOrDefault(e => e.Page == normTo);

        if (toEntry.Panel is null) return;

        var uiSettings = new Windows.UI.ViewManagement.UISettings();
        if (!uiSettings.AnimationsEnabled || fromEntry.Panel is null || fromEntry.Panel.Visibility != Visibility.Visible)
        {
            onSwapContent?.Invoke();
            SelectLibraryPageInstant(toPage);
            return;
        }

        CancelActiveLibraryTransition();

        bool isSamePanel = ReferenceEquals(fromEntry.Panel, toEntry.Panel);
        if (isSamePanel)
        {
            TransitionSameLibraryPanel(fromEntry.Panel, fromEntry.Transform, toPage, isForward, onSwapContent);
            return;
        }

        onSwapContent?.Invoke();

        var outgoingPanel = fromEntry.Panel;
        var outgoingTransform = fromEntry.Transform;
        var incomingPanel = toEntry.Panel;
        var incomingTransform = toEntry.Transform;

        _activeLibraryOutgoingPanel = outgoingPanel;
        _activeLibraryOutgoingTransform = outgoingTransform;
        _activeLibraryIncomingPanel = incomingPanel;
        _activeLibraryIncomingTransform = incomingTransform;

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
            if (!ReferenceEquals(_libraryTransitionStoryboard, storyboard)) return;

            storyboard.Stop();
            outgoingPanel.Visibility = Visibility.Collapsed;
            outgoingPanel.Opacity = 1.0;
            outgoingPanel.IsHitTestVisible = true;
            outgoingTransform.X = 0;

            incomingPanel.Opacity = 1.0;
            incomingTransform.X = 0;

            _activeLibraryOutgoingPanel = null;
            _activeLibraryOutgoingTransform = null;
            _activeLibraryIncomingPanel = null;
            _activeLibraryIncomingTransform = null;
            _pendingLibraryTargetPage = null;
            _libraryTransitionStoryboard = null;
        };

        _pendingLibraryTargetPage = toPage;
        _currentLibraryPage = toPage;
        _libraryTransitionStoryboard = storyboard;
        storyboard.Begin();
    }

    private void TransitionSameLibraryPanel(
        FrameworkElement panel,
        TranslateTransform transform,
        string toPage,
        bool isForward,
        Action? onSwapContent)
    {
        _activeLibraryOutgoingPanel = panel;
        _activeLibraryOutgoingTransform = transform;
        _activeLibraryIncomingPanel = panel;
        _activeLibraryIncomingTransform = transform;
        _pendingLibraryTargetPage = toPage;

        panel.Visibility = Visibility.Visible;
        panel.IsHitTestVisible = false;

        double exitEndX = isForward ? -36.0 : 36.0;
        double enterStartX = isForward ? 48.0 : -48.0;

        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        var exitStoryboard = new Storyboard();

        var outX = new DoubleAnimation
        {
            From = 0.0,
            To = exitEndX,
            Duration = new Duration(TimeSpan.FromMilliseconds(90)),
            EasingFunction = easeIn,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(outX, transform);
        Storyboard.SetTargetProperty(outX, "X");
        exitStoryboard.Children.Add(outX);

        var outFade = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(80)),
            EasingFunction = easeIn,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(outFade, panel);
        Storyboard.SetTargetProperty(outFade, "Opacity");
        exitStoryboard.Children.Add(outFade);

        exitStoryboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_libraryTransitionStoryboard, exitStoryboard)) return;

            exitStoryboard.Stop();
            panel.Opacity = 0.0;
            transform.X = enterStartX;

            // 交换内容
            onSwapContent?.Invoke();
            _currentLibraryPage = toPage;
            MainScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);

            var enterStoryboard = new Storyboard();

            var inX = new DoubleAnimation
            {
                From = enterStartX,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = easeOut,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(inX, transform);
            Storyboard.SetTargetProperty(inX, "X");
            enterStoryboard.Children.Add(inX);

            var inFade = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = easeOut,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(inFade, panel);
            Storyboard.SetTargetProperty(inFade, "Opacity");
            enterStoryboard.Children.Add(inFade);

            enterStoryboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_libraryTransitionStoryboard, enterStoryboard)) return;

                enterStoryboard.Stop();
                panel.Opacity = 1.0;
                panel.IsHitTestVisible = true;
                transform.X = 0;

                _activeLibraryOutgoingPanel = null;
                _activeLibraryOutgoingTransform = null;
                _activeLibraryIncomingPanel = null;
                _activeLibraryIncomingTransform = null;
                _pendingLibraryTargetPage = null;
                _libraryTransitionStoryboard = null;
            };

            _libraryTransitionStoryboard = enterStoryboard;
            enterStoryboard.Begin();
        };

        _libraryTransitionStoryboard = exitStoryboard;
        exitStoryboard.Begin();
    }

    private void NavigateLibrary(string page, bool? isForward = null)
    {
        if (page != _currentLibraryPage) CancelPlaylistDetailLoad();
        if (_currentPage == "library")
        {
            if (_currentLibraryPage != page && _pendingLibraryTargetPage != page)
            {
                bool forward = isForward ?? DetermineLibraryIsForward(_currentLibraryPage, page);
                UpdateSidebarLibrarySelection(page);
                TransitionLibraryPage(_currentLibraryPage, page, forward, onSwapContent: () =>
                {
                    NavigateLibrarySubTab(page);
                });
            }
        }
        else
        {
            Navigate(page);
        }
    }

    private void LibCategoryNavigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) NavigateLibrary(page, isForward: true);
    }

    private void LibraryBack_Click(object sender, RoutedEventArgs e) => GoBackInLibrary();
    private void LibraryBreadcrumbRoot_Click(object sender, RoutedEventArgs e) => GoBackInLibrary();

    private void GoBackInLibrary()
    {
        if (_playlistDetailSourcePage == "discover")
        {
            _playlistDetailSourcePage = null;
            Navigate("discover");
            return;
        }
        NavigateLibrary("library", isForward: false);
    }

    private void UpdateSidebarLibrarySelection(string page)
    {
        object? selected = null;
        if (page == "library:fond") selected = NavFond;
        else if (page == "library:recent") selected = NavRecent;
        else if (page.StartsWith("library:playlist:")) selected = FindSidebarPlaylistItem(page) ?? LibraryNav;
        else selected = LibraryNav;
        SyncNavigationSelection(selected);
    }

    private void UpdateLibRootUi()
    {
        if (!_services.Auth.IsSignedIn)
        {
            LibRootNotSignedInCard.Visibility = Visibility.Visible;
            UserPlaylistsList.Visibility = Visibility.Collapsed;
            LibRootFondSubtitle.Text = "登录后同步喜欢的音乐";
        }
        else
        {
            LibRootNotSignedInCard.Visibility = Visibility.Collapsed;
            UserPlaylistsList.Visibility = Visibility.Visible;
            LibRootFondSubtitle.Text = _fondSongs.Count > 0 ? $"{_fondSongs.Count} 首歌曲" : "查看我喜欢的歌曲";
            LibRootRecentSubtitle.Text = $"{_recentSongs.Count} 首历史歌曲";
        }
    }

    private void UpdateLibFondUi()
    {
        if (!_services.Auth.IsSignedIn)
        {
            LibFondNotSignedInCard.Visibility = Visibility.Visible;
            LibFondContentArea.Visibility = Visibility.Collapsed;
        }
        else
        {
            LibFondNotSignedInCard.Visibility = Visibility.Collapsed;
            LibFondContentArea.Visibility = Visibility.Visible;
        }
    }

    private void NavigateLibrarySubTab(string page)
    {
        if (page != _currentLibraryPage) CancelPlaylistDetailLoad();
        _currentLibraryPage = page;
        if (page == "library:fond")
        {
            UpdateLibFondUi();
            _ = LoadFondAsync();
        }
        else if (page == "library:recent")
        {
            _ = LoadRecentSongsAsync(showLoading: true);
        }
        else if (page.StartsWith("library:playlist:"))
        {
            var idStr = page["library:playlist:".Length..];
            if (long.TryParse(idStr, out var id))
            {
                _ = OpenPlaylistByIdAsync(id, autoPlay: false);
            }
        }
        else // page == "library"
        {
            UpdateLibRootUi();
            if (_services.Auth.IsSignedIn)
            {
                _ = LoadUserPlaylistsAsync();
            }
        }
    }

    private async Task LoadLibraryAsync()
    {
        if (_currentLibraryPage == "library:fond")
        {
            UpdateLibFondUi();
            if (_services.Auth.IsSignedIn) await LoadFondAsync();
        }
        else if (_currentLibraryPage == "library:recent")
        {
            await LoadRecentSongsAsync(showLoading: true);
        }
        else if (_currentLibraryPage == "library")
        {
            UpdateLibRootUi();
            if (_services.Auth.IsSignedIn) await LoadUserPlaylistsAsync();
        }
    }

    private void RefreshLibrary_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshAllPlaylistsAsync(showNotice: true);
    }

    private async void NavFond_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        Navigate("library:fond");
        if (_fondSongs.Count > 0)
        {
            await PlaySongListAsync(_fondSongs.Select(s => s.Song).ToList(), 0, navigateToPlaying: false);
        }
        else
        {
            await LoadFondAsync();
            if (_fondSongs.Count > 0)
            {
                await PlaySongListAsync(_fondSongs.Select(s => s.Song).ToList(), 0, navigateToPlaying: false);
            }
        }
    }

    private async void NavRecent_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        Navigate("library:recent");
        if (_recentSongs.Count > 0)
        {
            await PlaySongListAsync(_recentSongs.Select(s => s.Song).ToList(), 0, navigateToPlaying: false);
        }
        else
        {
            await LoadRecentSongsAsync(showLoading: true);
            if (_recentSongs.Count > 0)
            {
                await PlaySongListAsync(_recentSongs.Select(s => s.Song).ToList(), 0, navigateToPlaying: false);
            }
        }
    }

    private enum LibResultType
    {
        Success,
        Warning,
        Error,
        Info
    }

    private static Brush GetThemeBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var res) == true && res is Brush b)
            return b;
        return new SolidColorBrush(fallback);
    }

    private void ShowLibResultPill(string message, LibResultType type = LibResultType.Success)
    {
        _dispatcher.Post(() =>
        {
            _libResultPillCts?.Cancel();
            _libResultPillCts?.Dispose();
            _libResultPillCts = new CancellationTokenSource();
            var token = _libResultPillCts.Token;

            _libResultPillStoryboard?.Stop();
            _libResultPillStoryboard = null;

            LibResultText.Text = message;
            var (glyph, brush) = type switch
            {
                LibResultType.Warning => ("\uE7BA", GetThemeBrush("SystemFillColorCautionBrush", Windows.UI.Color.FromArgb(255, 157, 93, 0))),
                LibResultType.Error => ("\uEA39", GetThemeBrush("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(255, 196, 43, 28))),
                LibResultType.Info => ("\uE946", GetThemeBrush("SystemFillColorAttentionBrush", Windows.UI.Color.FromArgb(255, 0, 95, 184))),
                _ => ("\uE73E", GetThemeBrush("SystemFillColorSuccessBrush", Windows.UI.Color.FromArgb(255, 16, 124, 65)))
            };
            LibResultIcon.Glyph = glyph;
            LibResultIcon.Foreground = brush;

            LibResultPill.Visibility = Visibility.Visible;
            LibResultPill.Opacity = 0.0;
            LibResultPillTransform.Y = -10.0;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var inStoryboard = new Storyboard();

            var slideIn = new DoubleAnimation
            {
                From = -10.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = easeOut,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(slideIn, LibResultPillTransform);
            Storyboard.SetTargetProperty(slideIn, "Y");
            inStoryboard.Children.Add(slideIn);

            var fadeIn = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = easeOut,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fadeIn, LibResultPill);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            inStoryboard.Children.Add(fadeIn);

            _libResultPillStoryboard = inStoryboard;
            inStoryboard.Begin();

            var delayMs = type is LibResultType.Error or LibResultType.Warning ? 3500 : 2500;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token);
                    if (token.IsCancellationRequested) return;

                    _dispatcher.Post(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        HideLibResultPill();
                    });
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        });
    }

    private void HideLibResultPill()
    {
        _libResultPillCts?.Cancel();
        _libResultPillCts = null;

        if (LibResultPill is null || LibResultPill.Visibility != Visibility.Visible) return;

        _libResultPillStoryboard?.Stop();

        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var outStoryboard = new Storyboard();

        var slideOut = new DoubleAnimation
        {
            From = LibResultPillTransform.Y,
            To = -8.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = easeIn,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(slideOut, LibResultPillTransform);
        Storyboard.SetTargetProperty(slideOut, "Y");
        outStoryboard.Children.Add(slideOut);

        var fadeOut = new DoubleAnimation
        {
            From = LibResultPill.Opacity,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = easeIn,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(fadeOut, LibResultPill);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        outStoryboard.Children.Add(fadeOut);

        outStoryboard.Completed += (_, _) =>
        {
            LibResultPill.Visibility = Visibility.Collapsed;
            LibResultPill.Opacity = 0.0;
            LibResultPillTransform.Y = -10.0;
        };

        _libResultPillStoryboard = outStoryboard;
        outStoryboard.Begin();
    }

    private void DismissLibResultPillInstant()
    {
        _libResultPillCts?.Cancel();
        _libResultPillCts = null;
        _libResultPillStoryboard?.Stop();
        _libResultPillStoryboard = null;
        if (LibResultPill is not null)
        {
            LibResultPill.Visibility = Visibility.Collapsed;
            LibResultPill.Opacity = 0.0;
            LibResultPillTransform.Y = -10.0;
        }
    }

    private void LibResultPill_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        HideLibResultPill();
    }

    private async Task LoadFondAsync()
    {
        try
        {
            LibLoadingRing.Visibility = Visibility.Visible;
            LibLoadingText.Text = "正在获取我喜欢的音乐...";

            // 优先立即显示本地缓存歌曲
            var localFavorites = await _services.Favorites.GetFavoriteSongsAsync(_lifetime.Token);
            if (localFavorites.Count > 0)
            {
                _fondSongs.Clear();
                foreach (var s in localFavorites)
                {
                    _fondSongs.Add(new SongViewModel(s, isFavorite: true));
                }
                FondCount.Text = $"{localFavorites.Count} 首歌曲";
                LibRootFondSubtitle.Text = $"{localFavorites.Count} 首歌曲";
            }

            Playlist? fond = null;
            try
            {
                fond = await _services.Api.GetFondPlaylistAsync(_lifetime.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _services.Log.Write($"load fond error: {ex.Message}");
            }

            _currentFondPlaylist = fond;
            if (fond is not null)
            {
                FondTitle.Text = fond.Name;
                if (!string.IsNullOrWhiteSpace(fond.CoverUrl))
                {
                    FondCover.Source = new BitmapImage(new Uri(fond.CoverUrl));
                }

                try { await _services.Favorites.SyncAsync(_lifetime.Token); } catch { }
                var syncedSongs = await _services.Favorites.GetFavoriteSongsAsync(_lifetime.Token);
                _fondSongs.Clear();
                foreach (var song in syncedSongs)
                {
                    _fondSongs.Add(new SongViewModel(song, isFavorite: true));
                }
                FondCount.Text = $"{syncedSongs.Count} 首歌曲";
                LibRootFondSubtitle.Text = $"{syncedSongs.Count} 首歌曲";
            }
            else
            {
                FondTitle.Text = "我喜欢的音乐";
            }
        }
        catch (Exception ex)
        {
            _services.Log.Write($"load fond error: {ex.Message}");
        }
        finally
        {
            LibLoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadRecentSongsAsync(bool showLoading)
    {
        try
        {
            if (showLoading)
            {
                LibLoadingRing.Visibility = Visibility.Visible;
                LibLoadingText.Text = "正在获取最近播放...";
            }

            var recents = await _services.History.GetRecentSongsAsync(200, _lifetime.Token);
            _recentSongs.Clear();
            foreach (var song in recents)
            {
                _recentSongs.Add(new SongViewModel(song, _services.Favorites.IsFavorite(song.Id)));
            }
            RecentCount.Text = $"{recents.Count} 首历史歌曲";
            LibRootRecentSubtitle.Text = $"{recents.Count} 首历史歌曲";
        }
        catch (Exception ex)
        {
            _services.Log.Write($"load recents error: {ex.Message}");
        }
        finally
        {
            if (showLoading) LibLoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task PlaySongListAsync(IReadOnlyList<Song> songs, int startIndex, bool? navigateToPlaying = null)
    {
        if (songs.Count == 0 || startIndex < 0 || startIndex >= songs.Count) return;
        try
        {
            await _services.Coordinator.PlayQueueAsync(songs, startIndex, _lifetime.Token);
            var shouldNavigate = navigateToPlaying ?? _services.Settings.AutoNavigateToNowPlayingOnPlay;
            if (shouldNavigate && _currentPage != "playing")
            {
                Navigate("playing");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PlayAllFond_Click(object sender, RoutedEventArgs e)
    {
        _ = PlaySongListAsync(_fondSongs.Select(s => s.Song).ToList(), 0, navigateToPlaying: false);
    }

    private void FondSong_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SongViewModel song)
        {
            var list = _fondSongs.Select(s => s.Song).ToList();
            var idx = list.FindIndex(s => s.Id == song.Id);
            _ = PlaySongListAsync(list, Math.Max(0, idx));
        }
    }

    private void PlayFondSong_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id })
        {
            var list = _fondSongs.Select(s => s.Song).ToList();
            var idx = list.FindIndex(s => s.Id == id);
            _ = PlaySongListAsync(list, Math.Max(0, idx));
        }
    }

    private void PlayAllRecent_Click(object sender, RoutedEventArgs e)
    {
        _ = PlaySongListAsync(_recentSongs.Select(s => s.Song).ToList(), 0, navigateToPlaying: false);
    }

    private async void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        await _services.History.ClearRecentSongsAsync(_lifetime.Token);
        _recentSongs.Clear();
        RecentCount.Text = "0 首历史歌曲";
    }

    private void RecentSong_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SongViewModel song)
        {
            var list = _recentSongs.Select(s => s.Song).ToList();
            var idx = list.FindIndex(s => s.Id == song.Id);
            _ = PlaySongListAsync(list, Math.Max(0, idx));
        }
    }

    private void PlayRecentSong_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id })
        {
            var list = _recentSongs.Select(s => s.Song).ToList();
            var idx = list.FindIndex(s => s.Id == id);
            _ = PlaySongListAsync(list, Math.Max(0, idx));
        }
    }

}
