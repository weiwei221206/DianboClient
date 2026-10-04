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
    private readonly SearchViewModel _search;
    private ScrollViewer? _searchScrollViewer;
    private bool _isLoadingMoreSearch;

    private void SyncSearchUi()
    {
        HookSearchScrollViewer();
        SearchStatusText.Text = _search.Status;
        SearchRing.IsActive = _search.IsBusy && _search.Results.Count == 0;

        if (_search.IsBusy && _search.Results.Count > 0)
        {
            SearchListFooter.Visibility = Visibility.Visible;
            SearchFooterRing.Visibility = Visibility.Visible;
            SearchFooterRing.IsActive = true;
            SearchFooterText.Text = "正在加载更多歌曲…";
        }
        else if (!_search.HasMore && _search.Results.Count > 0)
        {
            SearchListFooter.Visibility = Visibility.Visible;
            SearchFooterRing.Visibility = Visibility.Collapsed;
            SearchFooterRing.IsActive = false;
            SearchFooterText.Text = $"已显示全部 {_search.Results.Count} 首歌曲";
        }
        else
        {
            SearchListFooter.Visibility = Visibility.Collapsed;
            SearchFooterRing.IsActive = false;
        }
    }



    private void Search_Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);
        MainScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);
        _search.OnQueryChanged(args.QueryText);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _searchScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);
        MainScrollViewer?.ChangeView(0, 0, null, disableAnimation: true);
        _search.OnQueryChanged(sender.Text);
    }

    private void HookSearchScrollViewer()
    {
        if (_searchScrollViewer is not null) return;
        _searchScrollViewer = FindVisualChild<ScrollViewer>(SearchResults);
        if (_searchScrollViewer is not null)
        {
            _searchScrollViewer.ViewChanged += SearchScrollViewer_ViewChanged;
        }
    }

    private void MainScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (SearchPage.Visibility == Visibility.Visible)
        {
            CheckSearchAutoLoadMore();
        }
    }

    private void SearchScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        CheckSearchAutoLoadMore();
    }

    private void CheckSearchAutoLoadMore()
    {
        if (_isLoadingMoreSearch || _search.IsBusy || !_search.HasMore || string.IsNullOrWhiteSpace(_search.Keyword))
            return;

        bool nearBottom = false;
        var sv = _searchScrollViewer ??= FindVisualChild<ScrollViewer>(SearchResults);
        if (sv is not null && sv.ScrollableHeight > 0)
        {
            double threshold = Math.Min(150, sv.ScrollableHeight * 0.3);
            if (sv.VerticalOffset >= sv.ScrollableHeight - threshold && sv.VerticalOffset > 0)
            {
                nearBottom = true;
            }
        }

        if (!nearBottom && MainScrollViewer is not null && MainScrollViewer.ScrollableHeight > 0)
        {
            double mainThreshold = Math.Min(100, MainScrollViewer.ScrollableHeight * 0.3);
            if (MainScrollViewer.VerticalOffset >= MainScrollViewer.ScrollableHeight - mainThreshold && MainScrollViewer.VerticalOffset > 0)
            {
                nearBottom = true;
            }
        }

        if (nearBottom)
        {
            _ = LoadMoreSearchResultsAsync();
        }
    }

    private async Task LoadMoreSearchResultsAsync()
    {
        if (_isLoadingMoreSearch || _search.IsBusy || !_search.HasMore) return;
        _isLoadingMoreSearch = true;
        try
        {
            await _search.LoadMoreAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _services.Log.Write($"search load more failed: {exception.Message}");
        }
        finally
        {
            _isLoadingMoreSearch = false;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent, string name) where T : FrameworkElement
    {
        if (parent is null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && match.Name == name) return match;
            var descendant = FindVisualChild<T>(child, name);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        var current = child;
        while (current != null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is T parent) return parent;
        }
        return null;
    }

    private void SearchResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SongViewModel song) _ = PlaySongAsync(song);
    }

    private void PlayResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long id }) return;
        var song = _search.Results.FirstOrDefault(item => item.Id == id);
        if (song is not null) _ = PlaySongAsync(song);
    }

}
