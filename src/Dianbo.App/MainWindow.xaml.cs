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
    private readonly AppServices _services;
    private readonly UiDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closing;
    private bool _isExplicitExit;
    private SystemTrayService? _trayService;
    private bool _restored;
    private int _networkDebounce;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var initialTheme = services.Settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => Root.ActualTheme
        };
        ThemeHelper.ActualTheme = initialTheme;
        if (services.Settings.Theme is "Light" or "Dark")
        {
            Root.RequestedTheme = initialTheme;
        }

        SetupCustomTitleBar();
        _dispatcher = new UiDispatcher(DispatcherQueue);
        _search = new SearchViewModel(services.Api, services.Favorites, _dispatcher.Post);

        _libraryPointerReleasedHandler = new PointerEventHandler(LibraryContentGrid_PointerReleased);
        _libraryPointerPressedHandler = new PointerEventHandler(LibraryContentGrid_PointerPressed);
        LibraryNav.Loaded += LibraryNav_Loaded;

        AppWindow.Title = "点波音乐";
        Root.Loaded += OnRootLoaded;
        Closed += OnClosed;
        AppWindow.Closing += AppWindow_Closing;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _trayService = new SystemTrayService(hwnd, this, services.Coordinator, DispatcherQueue, ExitApplication, ApplyPlayMode);

        SearchResults.ItemsSource = _search.Results;
        SearchResults.Loaded += (_, _) => HookSearchScrollViewer();
        MainScrollViewer.ViewChanged += MainScrollViewer_ViewChanged;
        LyricsList.ItemsSource = _lyrics;
        QueueListView.ItemsSource = _queueSongs;
        FondSongsList.ItemsSource = _fondSongs;
        RecentSongsList.ItemsSource = _recentSongs;
        UserPlaylistsList.ItemsSource = _userPlaylists;
        DetailPlaylistSongsList.ItemsSource = _detailPlaylistSongs;
        DiscoverSongsList.ItemsSource = _discoverSongs;
        DiscoverPlaylistsGrid.ItemsSource = _discoverPlaylists;

        _services.Coordinator.SnapshotChanged += (_, snapshot) => _dispatcher.Post(() => ApplySnapshot(snapshot));
        _services.Coordinator.LyricsChanged += (_, args) => _dispatcher.Post(() => ApplyLyrics(args));
        _services.Coordinator.Notice += (_, message) => _dispatcher.Post(() => _ = ShowDialogAsync("提示", message));
        _services.Coordinator.QueueChanged += (_, _) => _dispatcher.Post(() =>
        {
            SyncQueueUi();
            SaveQueueState();
        });
        _services.Coordinator.PlayModeChanged += (_, mode) => _dispatcher.Post(() =>
        {
            UpdatePlayModeUi(mode);
            SaveQueueState();
        });
        _services.Coordinator.SongStarted += (_, _) => _dispatcher.Post(SaveQueueState);

        SeekSlider.AddHandler(Slider.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(Slider.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(SeekSlider_PointerReleased), true);

        _search.PropertyChanged += (_, _) => _dispatcher.Post(SyncSearchUi);
        _services.History.HistoryChanged += (_, _) => _dispatcher.Post(() => _ = LoadRecentSongsAsync(showLoading: false));
        _services.Favorites.FavoritesChanged += (_, _) => _dispatcher.Post(OnFavoritesChanged);
        _services.Playlists.PlaylistsChanged += (_, _) => _dispatcher.Post(() => _ = PopulateSidebarPlaylistsAsync());
        _services.Auth.ProfileChanged += (_, _) => _dispatcher.Post(() =>
        {
            UpdateAccountUi();
            if (_services.Auth.IsSignedIn)
            {
                _ = PopulateSidebarPlaylistsAsync();
                _ = _services.Favorites.SyncAsync();
            }
            if (_currentPage == "library") _ = LoadLibraryAsync();
        });

        Root.PointerPressed += Root_PointerPressed;
        Root.KeyDown += Root_KeyDown;

        Navigate("discover");
        UpdateAccountUi();
        UpdateStorageUi();
        ApplySettingsToUi();
        RestoreQueueState();
        InitializeSystemMediaControls(hwnd);

        _ = Task.Run(async () =>
        {
            try
            {
                if (_services.Auth.IsSignedIn)
                {
                    await _services.Favorites.SyncAsync(_lifetime.Token);
                }
            }
            catch
            {
            }
        });

        _ = ProbeBackendOnStartupAsync();
    }

    private void SetupCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.Title = "点波音乐";
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            try
            {
                AppWindow.SetIcon(iconPath);
            }
            catch (Exception)
            {
            }
        }

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }

        AppTitleBar.Loaded += (_, _) => UpdateTitleBarPadding();
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarPadding();
        Activated += MainWindow_Activated;
        AppWindow.Changed += AppWindow_Changed;
        Root.ActualThemeChanged += (_, _) =>
        {
            ThemeHelper.ActualTheme = Root.ActualTheme;
            UpdateTitleBarTheme();
            RefreshThemeOnActiveViews();
        };
    }

    private void UpdateTitleBarPadding()
    {
        if (ExtendsContentIntoTitleBar && AppWindowTitleBar.IsCustomizationSupported())
        {
            var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale > 0)
            {
                TitleBarRightPaddingColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
                TitleBarLeftPaddingColumn.Width = new GridLength(AppWindow.TitleBar.LeftInset / scale);
            }
        }
    }

    private bool _isWindowActive = true;

    private void UpdateTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var isDark = Root?.ActualTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (isDark)
        {
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(25, 255, 255, 255);
            titleBar.ButtonPressedForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(140, 255, 255, 255);
        }
        else
        {
            titleBar.ButtonForegroundColor = Colors.Black;
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(25, 0, 0, 0);
            titleBar.ButtonPressedForegroundColor = Colors.Black;
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 0, 0, 0);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(140, 0, 0, 0);
        }

        UpdateTitleBarFocusVisuals();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _isWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        UpdateTitleBarFocusVisuals();
    }

    private void UpdateTitleBarFocusVisuals()
    {
        var isDark = Root?.ActualTheme == ElementTheme.Dark;
        if (!_isWindowActive)
        {
            AppTitleBarText.Foreground = isDark
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 0, 0));
            AppTitleBarIcon.Opacity = 0.55;
        }
        else
        {
            AppTitleBarText.Foreground = isDark
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Colors.Black);
            AppTitleBarIcon.Opacity = 1.0;
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            switch (sender.Presenter.Kind)
            {
                case AppWindowPresenterKind.FullScreen:
                    AppTitleBar.Visibility = Visibility.Collapsed;
                    break;
                case AppWindowPresenterKind.Overlapped:
                    AppTitleBar.Visibility = Visibility.Visible;
                    break;
            }
        }
    }

    private async Task ProbeBackendOnStartupAsync()
    {
        try
        {
            await _services.ProbeBackendAsync(_lifetime.Token);
        }
        catch (Exception)
        {
            // 探测失败不影响播放。
        }
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        var scale = Root.XamlRoot.RasterizationScale;
        UpdateTitleBarPadding();
        HookLibraryNavPointer();
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min((int)(1100 * scale), workArea.Width),
            Math.Min((int)(760 * scale), workArea.Height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(900 * scale), workArea.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(650 * scale), workArea.Height);
        }

        // 会话恢复放在窗口显示之后
        if (_restored) return;
        _restored = true;
        _ = LoadDiscoverContentAsync();
        try
        {
            var restored = await _services.Auth.RestoreAsync(_lifetime.Token);
            UpdateAccountUi();
            if (restored is not null)
            {
                if (_services.Auth.Profile is null)
                {
                    _ = _services.Auth.GetProfileAsync(_lifetime.Token);
                }
                _ = PopulateSidebarPlaylistsAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _services.Log.Write($"session restore failed: {exception.GetType().Name}");
        }
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isExplicitExit) return;
        if (string.Equals(_services.Settings.CloseBehavior, "MinimizeToTray", StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    public void ExitApplication()
    {
        _isExplicitExit = true;
        Close();
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closing) return;
        _closing = true;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        DisposeSystemMediaControls();
        _trayService?.Dispose();
        _trayService = null;
        SaveQueueState();
        _services.SaveSettings();
        _lifetime.Cancel();
        try
        {
            await _services.Coordinator.ShutdownAsync();
        }
        catch (Exception)
        {
        }
        _services.Dispose();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_closing || _lifetime.IsCancellationRequested) return;
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return;

        var debounceId = System.Threading.Interlocked.Increment(ref _networkDebounce);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500, _lifetime.Token).ConfigureAwait(false);
            if (debounceId != Volatile.Read(ref _networkDebounce)) return;

            _dispatcher.Post(async () =>
            {
                if (_closing || _lifetime.IsCancellationRequested) return;
                _services.Log.Write("network restored, re-syncing session and discover content");

                if (_services.Auth.IsSignedIn)
                {
                    try
                    {
                        await _services.Auth.GetProfileAsync(_lifetime.Token);
                        UpdateAccountUi();
                        await _services.Favorites.SyncAsync(_lifetime.Token);
                        await PopulateSidebarPlaylistsAsync();
                    }
                    catch (Exception ex)
                    {
                        _services.Log.Write($"network reconnect auth sync failed: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        var restored = await _services.Auth.RestoreAsync(_lifetime.Token);
                        UpdateAccountUi();
                        if (restored is not null)
                        {
                            await _services.Favorites.SyncAsync(_lifetime.Token);
                            await PopulateSidebarPlaylistsAsync();
                        }
                    }
                    catch
                    {
                    }
                }

                if (_discoverSongs.Count == 0 || DiscoverOfflineInfoBar.IsOpen)
                {
                    _ = LoadDiscoverContentAsync(forceRefresh: true);
                }

                if (_currentPage == "library")
                {
                    _ = LoadLibraryAsync();
                }
            });
        });
    }

}
