using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Dianbo.Core.Services;

namespace Dianbo.App.ViewModels;

public sealed class SearchViewModel : INotifyPropertyChanged
{
    private readonly IBodianApiClient _api;
    private readonly IFavoriteService? _favorites;
    private readonly object _sync = new();
    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _requestCts;
    private long _sequence;
    private string _keyword = string.Empty;
    private string _status = "下一首喜欢的歌，从这里开始";
    private bool _isBusy;
    private int _page;
    private bool _hasMore;

    private readonly Action<Action>? _dispatch;

    public SearchViewModel(IBodianApiClient api, IFavoriteService? favorites = null, Action<Action>? dispatch = null)
    {
        _api = api;
        _favorites = favorites;
        _dispatch = dispatch;
        if (_favorites is not null)
        {
            _favorites.FavoritesChanged += OnFavoritesChanged;
        }
    }

    private void OnFavoritesChanged(object? sender, EventArgs e)
    {
        if (_favorites is null) return;
        void Update()
        {
            foreach (var vm in Results)
            {
                vm.IsFavorite = _favorites.IsFavorite(vm.Id);
            }
        }

        if (_dispatch is not null)
        {
            _dispatch(Update);
        }
        else
        {
            Update();
        }
    }

    public ObservableCollection<SongViewModel> Results { get; } = [];

    public string Keyword
    {
        get => _keyword;
        private set
        {
            _keyword = value;
            Raise();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            Raise();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            Raise();
        }
    }

    public bool HasMore
    {
        get => _hasMore;
        set
        {
            _hasMore = value;
            Raise();
        }
    }

    public void OnQueryChanged(string keyword)
    {
        Interlocked.Increment(ref _sequence);
        _requestCts?.Cancel();
        Keyword = keyword.Trim();
        HasMore = false;
        IsBusy = false;
        Results.Clear();
        Status = Keyword.Length == 0 ? "输入歌曲、歌手或专辑名称" : "正在搜索…";
        CancellationTokenSource cts;
        lock (_sync)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            cts = _debounce;
        }
        _ = DebouncedSearchAsync(keyword, cts.Token);
    }

    private async Task DebouncedSearchAsync(string keyword, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(true);
            await SearchAsync(keyword, reset: true, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task SearchAsync(string keyword, bool reset, CancellationToken cancellationToken)
    {
        keyword = keyword.Trim();
        var sequence = Interlocked.Increment(ref _sequence);
        _requestCts?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _requestCts = request;
        cancellationToken = request.Token;
        Keyword = keyword;
        if (keyword.Length == 0)
        {
            Results.Clear();
            HasMore = false;
            IsBusy = false;
            _page = 0;
            _requestCts = null;
            Status = "输入歌曲、歌手或专辑名称";
            return;
        }

        if (reset) _page = 0;
        IsBusy = true;
        Status = "正在搜索…";
        try
        {
            var songs = await _api.SearchAsync(keyword, _page, 20, cancellationToken).ConfigureAwait(true);
            // 迟到的旧结果不能覆盖新关键词
            if (sequence != Volatile.Read(ref _sequence) || !string.Equals(keyword, Keyword, StringComparison.Ordinal)) return;

            if (reset) Results.Clear();
            foreach (var song in songs)
            {
                var isFav = _favorites?.IsFavorite(song.Id) ?? false;
                Results.Add(new SongViewModel(song, isFav));
            }
            HasMore = songs.Count > 0;
            Status = Results.Count == 0 ? $"没有找到与“{keyword}”相关的歌曲" : $"找到 {Results.Count} 首与“{keyword}”相关的歌曲";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 已被更新的关键词取代。
        }
        catch (OperationCanceledException)
        {
            if (sequence != Volatile.Read(ref _sequence)) return;
            Status = "搜索超时，请检查网络连接";
        }
        catch (BodianApiException exception)
        {
            if (sequence != Volatile.Read(ref _sequence)) return;
            Status = $"搜索失败：{exception.Message}";
        }
        catch (Exception exception)
        {
            if (sequence != Volatile.Read(ref _sequence)) return;
            Status = exception is TimeoutException ? "搜索超时，请检查网络连接" : $"搜索失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_requestCts, request)) _requestCts = null;
            if (sequence == Volatile.Read(ref _sequence)) IsBusy = false;
        }
    }

    /// <summary>加载下一页</summary>
    public async Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        if (!HasMore || IsBusy) return;
        _page++;
        var keyword = Keyword;
        var sequence = Interlocked.Increment(ref _sequence);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _requestCts = request;
        cancellationToken = request.Token;
        IsBusy = true;
        try
        {
            var songs = await _api.SearchAsync(keyword, _page, 20, cancellationToken).ConfigureAwait(true);
            if (sequence != Volatile.Read(ref _sequence) || !string.Equals(keyword, Keyword, StringComparison.Ordinal)) return;
            if (songs.Count > 0)
            {
                foreach (var song in songs)
                {
                    var isFav = _favorites?.IsFavorite(song.Id) ?? false;
                    Results.Add(new SongViewModel(song, isFav));
                }
                HasMore = true;
                Status = $"已加载 {Results.Count} 首";
            }
            else
            {
                HasMore = false;
                Status = $"已加载全部 {Results.Count} 首";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (sequence == Volatile.Read(ref _sequence))
            {
                _page--;
            }
        }
        catch (OperationCanceledException)
        {
            if (sequence == Volatile.Read(ref _sequence))
            {
                _page--;
                Status = "加载更多超时，请检查网络连接";
            }
        }
        catch (Exception exception)
        {
            if (sequence == Volatile.Read(ref _sequence))
            {
                _page--;
                Status = exception is TimeoutException ? "加载更多超时，请检查网络连接" : $"加载更多失败：{exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_requestCts, request)) _requestCts = null;
            if (sequence == Volatile.Read(ref _sequence)) IsBusy = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
