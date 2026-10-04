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
    private string _currentPage = "";
    private string? _pendingTopLevelTargetContainer;

    private bool _syncingNavigation;

    private Storyboard? _topLevelTransitionStoryboard;
    private FrameworkElement? _activeTopLevelOutgoingPanel;
    private TranslateTransform? _activeTopLevelOutgoingTransform;

    private (string Page, FrameworkElement Panel, TranslateTransform Transform)[] GetTopLevelPanelEntries() =>
    [
        ("discover", DiscoverPage, DiscoverTransform),
        ("search", SearchPage, SearchTransform),
        ("library", LibraryPage, LibraryTransform),
        ("queue", QueuePage, QueueTransform),
        ("playing", PlayingPage, PlayingTransform),
        ("settings", SettingsPage, SettingsTransform)
    ];

    private void CancelActiveTopLevelTransition()
    {
        _pendingTopLevelTargetContainer = null;
        var storyboard = _topLevelTransitionStoryboard;
        _topLevelTransitionStoryboard = null;
        storyboard?.Stop();

        foreach (var (name, panel, transform) in GetTopLevelPanelEntries())
        {
            panel.Visibility = name == _currentPage ? Visibility.Visible : Visibility.Collapsed;
            panel.Opacity = 1;
            panel.IsHitTestVisible = true;
            transform.X = 0;
            transform.Y = 0;
        }
        _activeTopLevelOutgoingPanel = null;
        _activeTopLevelOutgoingTransform = null;
    }

    private void SelectTopLevelPageInstant(string container)
    {
        _pendingTopLevelTargetContainer = null;
        CancelActiveTopLevelTransition();
        foreach (var (name, panel, transform) in GetTopLevelPanelEntries())
        {
            if (name == container)
            {
                panel.Visibility = Visibility.Visible;
                panel.Opacity = 1.0;
                panel.IsHitTestVisible = true;
                transform.Y = 0;
            }
            else
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 1.0;
                panel.IsHitTestVisible = true;
                transform.Y = 0;
            }
        }
        _currentPage = container;
        MainScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);
    }


    private void TransitionTopLevelPage(string fromContainer, string toContainer)
    {
        if (fromContainer == toContainer)
        {
            _pendingTopLevelTargetContainer = null;
            SelectTopLevelPageInstant(toContainer);
            return;
        }

        var entries = GetTopLevelPanelEntries();
        var fromEntry = entries.FirstOrDefault(e => e.Page == fromContainer);
        var toEntry = entries.FirstOrDefault(e => e.Page == toContainer);

        if (toEntry.Panel is null) return;

        var uiSettings = new Windows.UI.ViewManagement.UISettings();
        if (!uiSettings.AnimationsEnabled || fromEntry.Panel is null || fromEntry.Panel.Visibility != Visibility.Visible)
        {
            _pendingTopLevelTargetContainer = null;
            SelectTopLevelPageInstant(toContainer);
            return;
        }

        var outgoingPanel = fromEntry.Panel;
        var outgoingTransform = fromEntry.Transform;
        var incomingPanel = toEntry.Panel;
        var incomingTransform = toEntry.Transform;

        _activeTopLevelOutgoingPanel = outgoingPanel;
        _activeTopLevelOutgoingTransform = outgoingTransform;

        foreach (var (name, panel, transform) in entries)
        {
            if (panel != outgoingPanel && panel != incomingPanel)
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 1.0;
                panel.IsHitTestVisible = true;
                transform.Y = 0;
            }
        }

        // 入场初始状态
        incomingPanel.Visibility = Visibility.Visible;
        incomingPanel.IsHitTestVisible = true;
        incomingPanel.Opacity = 0.0;
        incomingTransform.Y = 24.0;

        outgoingPanel.IsHitTestVisible = false;
        outgoingPanel.Opacity = 1.0;
        outgoingTransform.Y = 0.0;

        MainScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();

        var inY = new DoubleAnimation
        {
            From = 24.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = easeOut,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(inY, incomingTransform);
        Storyboard.SetTargetProperty(inY, "Y");
        storyboard.Children.Add(inY);

        var inFade = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = easeOut,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(inFade, incomingPanel);
        Storyboard.SetTargetProperty(inFade, "Opacity");
        storyboard.Children.Add(inFade);

        var outFade = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(150)),
            EasingFunction = easeOut,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(outFade, outgoingPanel);
        Storyboard.SetTargetProperty(outFade, "Opacity");
        storyboard.Children.Add(outFade);

        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_topLevelTransitionStoryboard, storyboard)) return;

            storyboard.Stop();
            outgoingPanel.Visibility = Visibility.Collapsed;
            outgoingPanel.Opacity = 1.0;
            outgoingPanel.IsHitTestVisible = true;
            outgoingTransform.Y = 0;

            incomingPanel.Opacity = 1.0;
            incomingTransform.X = 0;
            incomingTransform.Y = 0;

            if (toContainer == "playing")
            {
                MainScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);
            }

            if (_activeTopLevelOutgoingPanel == outgoingPanel)
            {
                _activeTopLevelOutgoingPanel = null;
                _activeTopLevelOutgoingTransform = null;
            }
            _pendingTopLevelTargetContainer = null;
            _topLevelTransitionStoryboard = null;
        };

        _topLevelTransitionStoryboard = storyboard;
        storyboard.Begin();
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) Navigate(page);
    }

    public void Navigate(string page)
    {
        if (_currentPage != "library" || page != _currentLibraryPage)
            CancelPlaylistDetailLoad();
        bool isLibrary = page == "library" || page.StartsWith("library:");
        var container = isLibrary ? "library" : (IsSettingsPage(page) ? "settings" : page);
        var targetSettingsPage = IsSettingsPage(page) ? page : "settings";

        // 如果已经在设置页容器内部，切换设置子项直接进行平滑动画过渡
        if (container == "settings" && _currentPage == "settings")
        {
            if (_currentSettingsPage != targetSettingsPage)
            {
                if (targetSettingsPage == "settings") _settingsHistory.Clear();
                bool isForward = _currentSettingsPage == "settings" && targetSettingsPage != "settings";
                TransitionSettingsPage(_currentSettingsPage, targetSettingsPage, isForward);
            }
            return;
        }

        // 如果已经在我的音乐容器内部，切换分类或歌单直接进行平滑动画过渡
        if (container == "library" && _currentPage == "library")
        {
            if (_currentLibraryPage != page && _pendingLibraryTargetPage != page)
            {
                bool isForward = DetermineLibraryIsForward(_currentLibraryPage, page);
                UpdateSidebarLibrarySelection(page);
                TransitionLibraryPage(_currentLibraryPage, page, isForward, onSwapContent: () =>
                {
                    NavigateLibrarySubTab(page);
                });
            }
            return;
        }

        if (container == _currentPage || container == _pendingTopLevelTargetContainer)
        {
            return;
        }

        CancelActiveTopLevelTransition();
        CancelActiveSettingsTransition();
        CancelActiveLibraryTransition();

        var previousContainer = _currentPage;
        _currentPage = container;

        if (isLibrary)
        {
            UpdateSidebarLibrarySelection(page);
        }
        else
        {
            var selected = container switch
            {
                "search" => SearchNav,
                "library" => LibraryNav,
                "queue" => QueueNav,
                "playing" => PlayingNav,
                "settings" => Navigation.SettingsItem,
                _ => DiscoverNav
            };
            SyncNavigationSelection(selected);
        }

        if (container == "settings")
        {
            SelectSettingsPageInstant(targetSettingsPage);
        }
        else if (container == "library")
        {
            SelectLibraryPageInstant(page);
            NavigateLibrarySubTab(page);
        }
        else if (container == "search")
        {
            SearchBox.Focus(FocusState.Programmatic);
        }
        else if (container == "discover" && !_discoverLoaded)
        {
            _ = LoadDiscoverContentAsync();
        }
        else if (container == "queue")
        {
            SyncQueueUi();
        }
        else if (container == "playing")
        {
            OnNavigatedToPlaying();
        }

        if (container != "playing")
        {
            OnNavigatedAwayFromPlaying();
        }

        if (string.IsNullOrEmpty(previousContainer) || previousContainer == container)
        {
            SelectTopLevelPageInstant(container);
        }
        else
        {
            _pendingTopLevelTargetContainer = container;
            TransitionTopLevelPage(previousContainer, container);
        }
    }

    private void SyncNavigationSelection(object? selected)
    {
        if (ReferenceEquals(Navigation.SelectedItem, selected)) return;
        _syncingNavigation = true;
        try
        {
            Navigation.SelectedItem = selected;
        }
        finally
        {
            _syncingNavigation = false;
        }
    }

    private void NavigateSettings(string page, bool recordHistory, bool? forceForward = null)
    {
        if (!IsSettingsPage(page)) return;

        if (_currentPage == "settings" && _currentSettingsPage == page) return;

        bool isForward = forceForward ?? (recordHistory || (_currentSettingsPage == "settings" && page != "settings"));

        if (recordHistory && _currentPage == "settings")
        {
            _settingsHistory.Push(_currentSettingsPage);
        }

        if (_currentPage == "settings")
        {
            TransitionSettingsPage(_currentSettingsPage, page, isForward);
        }
        else
        {
            Navigate(page);
        }
    }

    private static bool IsSettingsPage(string page) => SettingsPages.Contains(page);

    private void SettingsNavigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) NavigateSettings(page, recordHistory: true, forceForward: true);
    }

    private async void PaneAccount_Click(object sender, RoutedEventArgs e)
    {
        if (!_services.Auth.IsSignedIn)
        {
            await ShowQrLoginAsync();
            return;
        }
        _settingsHistory.Clear();
        if (_currentPage == "settings")
        {
            NavigateSettings("settings-account", recordHistory: false, forceForward: true);
        }
        else
        {
            Navigate("settings-account");
        }
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e) => GoBackInSettings();

    private void SettingsBreadcrumbRoot_Click(object sender, RoutedEventArgs e) => GoBackInSettings();

    private void SettingsBreadcrumbSettings_Click(object sender, RoutedEventArgs e)
    {
        _settingsHistory.Clear();
        NavigateSettings("settings", recordHistory: false, forceForward: false);
    }

    private void SettingsBreadcrumbLyrics_Click(object sender, RoutedEventArgs e)
    {
        _settingsHistory.Clear();
        _settingsHistory.Push("settings");
        NavigateSettings("settings-lyrics", recordHistory: false, forceForward: false);
    }

    private void GoBackInSettings()
    {
        if (_settingsHistory.Count > 0)
        {
            NavigateSettings(_settingsHistory.Pop(), recordHistory: false, forceForward: false);
        }
        else
        {
            NavigateSettings("settings", recordHistory: false, forceForward: false);
        }
    }

    private void Root_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (point.Properties.IsXButton1Pressed)
        {
            if (_currentPage == "settings" && _currentSettingsPage != "settings")
            {
                GoBackInSettings();
                e.Handled = true;
            }
        }
    }

    private void Root_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Left && e.KeyStatus.IsMenuKeyDown)
        {
            if (_currentPage == "settings" && _currentSettingsPage != "settings")
            {
                GoBackInSettings();
                e.Handled = true;
            }
        }
    }

    private void Navigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            _settingsHistory.Clear();
            NavigateSettings("settings", recordHistory: false, forceForward: false);
            return;
        }

        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string page)
        {
            if (ReferenceEquals(item, LibraryNav))
            {
                // 保留用户设定的展开/收起状态，点击选项卡本身不改变展开状态
                if (LibraryNav.IsExpanded != _libraryNavUserExpanded)
                {
                    _isRestoringExpandedState = true;
                    try
                    {
                        LibraryNav.IsExpanded = _libraryNavUserExpanded;
                    }
                    finally
                    {
                        _isRestoringExpandedState = false;
                    }
                }
            }
            Navigate(page);
        }
    }

    private void Navigation_Expanding(NavigationView sender, NavigationViewItemExpandingEventArgs args)
    {
        if (_isRestoringExpandedState) return;
        if (ReferenceEquals(args.ExpandingItemContainer, LibraryNav))
        {
            _libraryNavUserExpanded = true;
        }
    }

    private void Navigation_Collapsed(NavigationView sender, NavigationViewItemCollapsedEventArgs args)
    {
        if (_isRestoringExpandedState) return;
        if (ReferenceEquals(args.CollapsedItemContainer, LibraryNav))
        {
            _libraryNavUserExpanded = false;
        }
    }

    private void LibraryNav_Loaded(object sender, RoutedEventArgs e)
    {
        HookLibraryNavPointer();
    }

    private void HookLibraryNavPointer()
    {
        var contentGrid = FindVisualChild<FrameworkElement>(LibraryNav, "ContentGrid")
                          ?? FindVisualChild<FrameworkElement>(LibraryNav, "PresenterContentRootGrid");
        if (contentGrid != null && !ReferenceEquals(_hookedLibraryContentGrid, contentGrid))
        {
            if (_hookedLibraryContentGrid != null)
            {
                _hookedLibraryContentGrid.RemoveHandler(UIElement.PointerReleasedEvent, _libraryPointerReleasedHandler);
                _hookedLibraryContentGrid.RemoveHandler(UIElement.PointerPressedEvent, _libraryPointerPressedHandler);
            }
            _hookedLibraryContentGrid = contentGrid;
            contentGrid.AddHandler(UIElement.PointerPressedEvent, _libraryPointerPressedHandler, true);
            contentGrid.AddHandler(UIElement.PointerReleasedEvent, _libraryPointerReleasedHandler, true);
        }
    }

    private void LibraryContentGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _lastPointerDownOnChevron = IsPointerOnLibraryChevron(e);
    }

    private void LibraryContentGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool isChevron = IsPointerOnLibraryChevron(e);
        if (_lastPointerDownOnChevron || isChevron)
        {
            return;
        }

        if (sender is FrameworkElement fe)
        {
            var pt = e.GetCurrentPoint(fe).Position;
            if (pt.X < 0 || pt.X > fe.ActualWidth || pt.Y < 0 || pt.Y > fe.ActualHeight)
            {
                return;
            }
        }

        e.Handled = true;

        var presenter = FindVisualChild<Control>(LibraryNav, "NavigationViewItemPresenter");
        if (presenter != null)
        {
            VisualStateManager.GoToState(presenter, "PointerOverSelected", true);
        }

        // 切换到我的音乐页面并同步选中状态
        Navigate("library");
        SyncNavigationSelection(LibraryNav);
    }

    private bool IsPointerOnLibraryChevron(PointerRoutedEventArgs e)
    {
        var chevron = FindVisualChild<FrameworkElement>(LibraryNav, "ExpandCollapseChevron");
        if (chevron != null && chevron.Visibility == Visibility.Visible)
        {
            var cur = e.OriginalSource as DependencyObject;
            while (cur != null && !ReferenceEquals(cur, LibraryNav))
            {
                if (ReferenceEquals(cur, chevron) || (cur is FrameworkElement f && f.Name == "ExpandCollapseChevron"))
                {
                    return true;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }

            try
            {
                var pt = e.GetCurrentPoint(chevron).Position;
                if (pt.X >= 0 && pt.X <= chevron.ActualWidth && pt.Y >= 0 && pt.Y <= chevron.ActualHeight)
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        try
        {
            var ptItem = e.GetCurrentPoint(LibraryNav).Position;
            if (ptItem.X >= LibraryNav.ActualWidth - 44 && ptItem.X <= LibraryNav.ActualWidth)
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (DiscoverPage is null) return;

        if (_syncingNavigation) return;
        if (args.IsSettingsSelected)
        {
            if (_currentPage == "settings" && _currentSettingsPage != "settings")
            {
                _settingsHistory.Clear();
                NavigateSettings("settings", recordHistory: false, forceForward: false);
            }
            else
            {
                _settingsHistory.Clear();
                Navigate("settings");
            }
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: string page }) Navigate(page);
    }

}
