using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Dianbo.Core.Lyrics;
using Dianbo.Core.Models;
using Dianbo.Core.Services;

namespace Dianbo.Core.Playback;

public sealed class PlaybackCoordinator
{
    private readonly IBodianApiClient _api;
    private readonly IPlaybackBackend _backend;
    private readonly PlaybackTimeouts _timeouts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly object _requestSync = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _recommendationCts;
    private bool _shuttingDown;
    private int _remainingRecovery;
    private long _generation;

    private void CancelLoad()
    {
        lock (_requestSync)
        {
            Interlocked.Increment(ref _generation);
            _recommendationCts?.Cancel();
            _loadCts?.Cancel();
        }
    }

    private void CancelRecommendation()
    {
        lock (_requestSync) _recommendationCts?.Cancel();
    }

    private List<Song> _queue = [];
    private int _queueIndex = -1;
    private int _volume = 60;
    private bool _autoAdvancing;
    private CancellationTokenSource? _observeCts;
    private Task? _observeTask;
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Initial;
    private PlaybackMode _playMode = PlaybackMode.RepeatAll;
    private readonly IAudioCacheService? _cacheService;
    private readonly List<int> _shuffleHistory = [];
    private bool _recommendationRadio;

    public bool IsRecommendationRadio => _recommendationRadio;

    public PlaybackCoordinator(
        IBodianApiClient api,
        IPlaybackBackend backend,
        IAudioCacheService? cacheService = null,
        PlaybackTimeouts? timeouts = null,
        int volume = 60,
        bool enableAudioCache = true,
        int audioCacheLimitMb = 1024)
    {
        _api = api;
        _backend = backend;
        _cacheService = cacheService;
        _timeouts = timeouts ?? new PlaybackTimeouts();
        _volume = Math.Clamp(volume, 0, 100);
        _snapshot = PlaybackSnapshot.Initial with { Volume = _volume };
        EnableAudioCache = enableAudioCache;
        AudioCacheLimitMb = audioCacheLimitMb;
    }

    public IAudioCacheService? CacheService => _cacheService;

    public bool EnableAudioCache { get; set; } = true;

    public int AudioCacheLimitMb { get; set; } = 1024;

    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;

    public event EventHandler<LyricsChangedEventArgs>? LyricsChanged;

    public event EventHandler<string>? Notice;

    public event EventHandler<Song>? SongStarted;

    public event EventHandler<PlaybackMode>? PlayModeChanged;

    public event EventHandler? QueueChanged;

    public PlaybackSnapshot Snapshot => _snapshot;
    public IReadOnlyList<Song> Queue => _queue;
    public int QueueIndex => _queueIndex;
    public Song? CurrentSong => _queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex] : null;
    public LyricDocument Lyrics { get; private set; } = LyricDocument.Empty;
    public int Volume => _volume;

    public string PreferredQuality { get; set; } = "2000kflac";

    public AudioSource? CurrentAudioSource => _snapshot.AudioSource;

    public PlaybackMode PlayMode
    {
        get => _playMode;
        set
        {
            if (_playMode == value) return;
            _playMode = value;
            PlayModeChanged?.Invoke(this, value);
        }
    }

    public bool SkipUnplayable { get; set; }

    public bool RetryOnFailure { get; set; } = true;

    public Task ReloadCurrentAsync(CancellationToken cancellationToken) => PlayCurrentAsync(cancellationToken);

    public void RestoreState(IReadOnlyList<Song> songs, int index, TimeSpan position, TimeSpan duration, PlaybackMode? mode = null)
    {
        if (songs.Count == 0) return;
        _gate.Wait();
        try
        {
            _queue = [.. songs];
            _queueIndex = Math.Clamp(index, 0, _queue.Count - 1);
            _recommendationRadio = false;
            _shuffleHistory.Clear();
            if (mode.HasValue)
            {
                _playMode = mode.Value;
            }
            if (_queueIndex >= 0 && _queueIndex < _queue.Count)
            {
                var song = _queue[_queueIndex];
                var generation = Interlocked.Increment(ref _generation);
                var effectiveDuration = duration > TimeSpan.Zero
                    ? duration
                    : (song.Duration ?? TimeSpan.Zero);

                _snapshot = _snapshot with
                {
                    Generation = generation,
                    SongId = song.Id,
                    Position = TimeSpan.Zero,
                    Duration = effectiveDuration,
                    State = PlaybackState.Paused,
                    IsPaused = true,
                    EndReason = PlaybackEndReason.None,
                    Error = null
                };

                _ = Task.Run(() => FetchLyricsAsync(song, generation, CancellationToken.None));
            }
        }
        finally
        {
            _gate.Release();
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);
        Publish(_snapshot);
    }

    public async Task PlayQueueAsync(IReadOnlyList<Song> songs, int index, CancellationToken cancellationToken, bool recommendationRadio = false)
    {
        if (songs.Count == 0 || index < 0 || index >= songs.Count) return;
        CancelRecommendation();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _queue = [.. songs];
            _queueIndex = index;
            _recommendationRadio = recommendationRadio;
            _shuffleHistory.Clear();
        }
        finally
        {
            _gate.Release();
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
        await PlayCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PlayAtAsync(int index, CancellationToken cancellationToken)
    {
        CancelRecommendation();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (index < 0 || index >= _queue.Count) return;
            if (PlayMode == PlaybackMode.Shuffle && _queueIndex >= 0 && _queueIndex < _queue.Count && _queueIndex != index)
            {
                _shuffleHistory.Add(_queueIndex);
                if (_shuffleHistory.Count > 100) _shuffleHistory.RemoveAt(0);
            }
            _queueIndex = index;
        }
        finally
        {
            _gate.Release();
        }
        await PlayCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAtAsync(int index, CancellationToken cancellationToken)
    {
        var needStop = false;
        var needPlayCurrent = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (index < 0 || index >= _queue.Count) return;

            _shuffleHistory.RemoveAll(i => i == index);
            for (var i = 0; i < _shuffleHistory.Count; i++)
            {
                if (_shuffleHistory[i] > index) _shuffleHistory[i]--;
            }

            if (_queue.Count == 1)
            {
                _queue.Clear();
                _queueIndex = -1;
                _recommendationRadio = false;
                _shuffleHistory.Clear();
                needStop = true;
            }
            else if (index == _queueIndex)
            {
                _queue.RemoveAt(index);
                if (_queueIndex >= _queue.Count)
                {
                    _queueIndex = 0;
                }
                needPlayCurrent = true;
            }
            else if (index < _queueIndex)
            {
                _queue.RemoveAt(index);
                _queueIndex--;
            }
            else
            {
                _queue.RemoveAt(index);
            }
        }
        finally
        {
            _gate.Release();
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);

        if (needStop)
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (needPlayCurrent)
        {
            await PlayCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ClearQueueAsync(CancellationToken cancellationToken)
    {
        CancelLoad();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _queue.Clear();
            _queueIndex = -1;
            _recommendationRadio = false;
            _shuffleHistory.Clear();
        }
        finally
        {
            _gate.Release();
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);
        await StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(Song song, bool playNext = false, CancellationToken cancellationToken = default)
    {
        CancelRecommendation();
        var needPlay = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_queue.Count == 0)
            {
                _queue.Add(song);
                _queueIndex = 0;
                needPlay = true;
            }
            else if (playNext && _queueIndex >= 0 && _queueIndex < _queue.Count)
            {
                _queue.Insert(_queueIndex + 1, song);
            }
            else
            {
                _queue.Add(song);
            }
        }
        finally
        {
            _gate.Release();
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);

        if (needPlay)
        {
            await PlayCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task EnqueueRangeAsync(IReadOnlyList<Song> songs, bool playNext = false, CancellationToken cancellationToken = default)
    {
        if (songs.Count == 0) return;
        CancelRecommendation();
        var needPlay = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_queue.Count == 0)
            {
                _queue.AddRange(songs);
                _queueIndex = 0;
                needPlay = true;
            }
            else if (playNext && _queueIndex >= 0 && _queueIndex < _queue.Count)
            {
                _queue.InsertRange(_queueIndex + 1, songs);
            }
            else
            {
                _queue.AddRange(songs);
            }
        }
        finally
        {
            _gate.Release();
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);

        if (needPlay)
        {
            await PlayCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task NextAsync(CancellationToken cancellationToken) => MoveAsync(+1, userInitiated: true, cancellationToken);

    public Task PreviousAsync(CancellationToken cancellationToken) => MoveAsync(-1, userInitiated: true, cancellationToken);

    public async Task StartRecommendationRadioAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource request;
        long expectedGeneration;
        lock (_requestSync)
        {
            if (_shuttingDown) return;
            _recommendationCts?.Cancel();
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _recommendationCts = request;
            expectedGeneration = _generation;
        }
        try
        {
            var song = await FetchRecommendationAsync(new HashSet<long>(), null, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            if (song is null)
            {
                Notice?.Invoke(this, "暂时没有获取到推荐歌曲，请稍后重试。");
                return;
            }

            await _gate.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                lock (_requestSync)
                {
                    if (request.IsCancellationRequested || _shuttingDown || expectedGeneration != _generation) return;
                    _queue = [song];
                    _queueIndex = 0;
                    _recommendationRadio = true;
                    _shuffleHistory.Clear();
                }
            }
            finally { _gate.Release(); }
            QueueChanged?.Invoke(this, EventArgs.Empty);
            await PlayCurrentAsync(request.Token, expectedGeneration: expectedGeneration).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            lock (_requestSync)
            {
                if (ReferenceEquals(_recommendationCts, request)) _recommendationCts = null;
                request.Dispose();
            }
        }
    }

    private async Task<Song?> FetchRecommendationAsync(IReadOnlySet<long> recentIds, long? currentId, CancellationToken cancellationToken)
    {
        Song? fallback = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var page = attempt == 4 ? 1 : Random.Shared.Next(1, 26);
            try
            {
                var songs = await _api.GetRecommendSongsAsync(page, 20, cancellationToken).ConfigureAwait(false);
                var candidates = songs.Where(song => song.Id > 0 && !recentIds.Contains(song.Id)).ToArray();
                if (candidates.Length > 0) return candidates[Random.Shared.Next(candidates.Length)];
                var older = songs.Where(song => song.Id > 0 && song.Id != currentId).ToArray();
                if (older.Length > 0) fallback = older[Random.Shared.Next(older.Length)];
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                break;
            }
        }
        return fallback;
    }

    public async Task TogglePauseAsync(CancellationToken cancellationToken)
    {
        if (_snapshot.State is PlaybackState.Idle or PlaybackState.Failed)
        {
            if (CurrentSong is not null)
            {
                await PlayCurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (!_backend.IsRunning || _snapshot.AudioSource is null)
        {
            await PlayCurrentAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var target = !_snapshot.IsPaused;
        await _backend.SetPausedAsync(target, cancellationToken).ConfigureAwait(false);
        Publish(_snapshot with { IsPaused = target, State = target ? PlaybackState.Paused : PlaybackState.Playing });
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        if (_snapshot.State is PlaybackState.Idle or PlaybackState.Failed) return;
        var clamped = position;
        if (clamped < TimeSpan.Zero) clamped = TimeSpan.Zero;
        if (_snapshot.Duration > TimeSpan.Zero && clamped > _snapshot.Duration) clamped = _snapshot.Duration;
        Publish(_snapshot with { IsSeeking = true, Position = clamped });
        try
        {
            if (_backend.IsRunning && _snapshot.AudioSource is not null)
            {
                await _backend.SeekAsync(clamped, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Publish(_snapshot with { IsSeeking = false, Position = clamped });
        }
    }

    public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_backend.IsRunning) await _backend.SetVolumeAsync(_volume, cancellationToken).ConfigureAwait(false);
        Publish(_snapshot with { Volume = _volume });
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancelLoad();
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish(_snapshot with { State = PlaybackState.Stopping, EndReason = PlaybackEndReason.Stopped });
            await CancelObservationAsync().ConfigureAwait(false);
            if (_backend.IsRunning)
            {
                try { await _backend.StopMediaAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception) {  }
            }
            Lyrics = LyricDocument.Empty;
            LyricsChanged?.Invoke(this, new LyricsChangedEventArgs(Lyrics, null, 0));
            Publish(_snapshot with
            {
                State = PlaybackState.Idle,
                SongId = null,
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero,
                IsPaused = false,
                EndReason = PlaybackEndReason.Stopped,
                Error = null,
                AudioSource = null
            });
        }
        finally { _loadGate.Release(); }
    }

    public async Task ShutdownAsync()
    {
        lock (_requestSync) _shuttingDown = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        try { await _backend.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) {  }
        await _backend.DisposeAsync().ConfigureAwait(false);
    }

    public LyricGroup? CurrentLyricGroup() => Lyrics.GroupAt(_snapshot.Position);

    private async Task PlayCurrentAsync(CancellationToken cancellationToken, int retryBudget = 1, long? expectedGeneration = null)
    {
        CancellationTokenSource request;
        long generation;
        lock (_requestSync)
        {
            if (_shuttingDown || CurrentSong is null || (expectedGeneration.HasValue && expectedGeneration.Value != _generation)) return;
            _loadCts?.Cancel();
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loadCts = request;
            generation = Interlocked.Increment(ref _generation);
        }
        var entered = false;
        try
        {
            await _loadGate.WaitAsync(request.Token).ConfigureAwait(false);
            entered = true;
            request.Token.ThrowIfCancellationRequested();
            await CancelObservationAsync().ConfigureAwait(false);
            if (_backend.IsRunning)
            {
                try { await _backend.StopMediaAsync(request.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { await _backend.StopAsync(request.Token).ConfigureAwait(false); }
            }
            var visited = new HashSet<long>();
            while (CurrentSong is { } song && visited.Add(song.Id))
            {
                Lyrics = LyricDocument.Empty;
                LyricsChanged?.Invoke(this, new LyricsChangedEventArgs(Lyrics, song.Id, generation));
                Publish(_snapshot with { Generation = generation, SongId = song.Id,
                    State = PlaybackState.Resolving, Position = TimeSpan.Zero,
                    Duration = song.Duration ?? TimeSpan.Zero, IsPaused = false, IsSeeking = false,
                    EndReason = PlaybackEndReason.None, Error = null, AudioSource = null });
                bool skip = false;
                for (int attempt = 0; ; attempt++)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
                    deadline.CancelAfter(_timeouts.Api + _timeouts.MediaOpen);
                    try
                    {
                        _remainingRecovery = RetryOnFailure ? Math.Max(0, retryBudget - attempt) : 0;
                        await AttemptLoadAsync(song, generation, deadline.Token).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception error)
                    {
                        if (request.IsCancellationRequested || generation != Volatile.Read(ref _generation)) return;
                        if (error is BodianApiException { IsPermissionDenied: true } && SkipUnplayable)
                        {
                            skip = true;
                            Notice?.Invoke(this, $"《{song.Name}》无法播放，正在检查下一首。");
                            break;
                        }
                        bool retryable = error is not BodianApiException apiError || apiError.Retryable;
                        if (RetryOnFailure && retryable && error is not InvalidOperationException && attempt < retryBudget)
                        {
                            Notice?.Invoke(this, "播放失败，正在重新获取地址并重试一次。");
                            continue;
                        }
                        if (_backend.IsRunning)
                        {
                            try { await _backend.StopMediaAsync(request.Token).ConfigureAwait(false); } catch { }
                        }
                        Publish(_snapshot with { State = PlaybackState.Failed,
                            Error = error is OperationCanceledException ? "加载超时" : (error is HttpRequestException ? "网络连接失败，请检查网络设置" : error.Message) });
                        Notice?.Invoke(this, $"《{song.Name}》播放失败：{_snapshot.Error}");
                        return;
                    }
                }
                if (!skip) return;
                await _gate.WaitAsync(request.Token).ConfigureAwait(false);
                try
                {
                    var next = Enumerable.Range(1, _queue.Count)
                        .Select(offset => (_queueIndex + offset) % _queue.Count)
                        .FirstOrDefault(index => !visited.Contains(_queue[index].Id), -1);
                    if (next < 0) break;
                    _queueIndex = next;
                }
                finally { _gate.Release(); }
            }
            Publish(_snapshot with { State = PlaybackState.Failed, Error = "队列中没有可播放的歌曲" });
            Notice?.Invoke(this, "队列中没有可播放的歌曲，已停止自动跳过。");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (entered) _loadGate.Release();
            lock (_requestSync)
            {
                if (ReferenceEquals(_loadCts, request)) _loadCts = null;
                request.Dispose();
            }
        }
    }

    private async Task AttemptLoadAsync(Song song, long generation, CancellationToken cancellationToken)
    {
        if (!_backend.IsConfigured)
            throw new InvalidOperationException("尚未找到音频后端 mpv，请在“设置 → 播放与音质 → 音频后端”中指定");

        var rightsTask = SafeCheckRightAsync(song.Id, cancellationToken);
        var lyricsTask = FetchLyricsAsync(song, generation, cancellationToken);

        var candidates = GetQualityCandidates(PreferredQuality);
        AudioSource? source = null;
        string? resolvedQuality = null;
        Exception? lastException = null;

        if (_cacheService != null && EnableAudioCache)
        {
            foreach (var quality in candidates)
            {
                var cachedPath = _cacheService.GetCachedFilePath(song.Id, quality);
                if (cachedPath != null && File.Exists(cachedPath))
                {
                    var fi = new FileInfo(cachedPath);
                    var ext = Path.GetExtension(cachedPath).TrimStart('.').ToLowerInvariant();
                    var brNum = quality.Contains("2000") ? 2000 : (quality.Contains("320") ? 320 : 128);
                    source = new AudioSource
                    {
                        SongId = song.Id,
                        Url = cachedPath,
                        Format = ext,
                        Bitrate = brNum,
                        Size = fi.Length,
                        Duration = song.Duration,
                        IsFromCache = true
                    };
                    resolvedQuality = quality;
                    break;
                }
            }
        }

        if (source is null)
        {
            foreach (var quality in candidates)
            {
                if (generation != Volatile.Read(ref _generation)) return;
                try
                {
                    source = await _api.ResolveAudioSourceAsync(song.Id, quality, cancellationToken).ConfigureAwait(false);
                    if (source is not null && !string.IsNullOrWhiteSpace(source.Url))
                    {
                        resolvedQuality = quality;
                        break;
                    }
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastException = new TimeoutException("在线解析音频超时", ex);
                    break;
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;
                }
            }
        }

        if ((source is null || string.IsNullOrWhiteSpace(source.Url)) && _cacheService != null && EnableAudioCache)
        {
            var fallbackCached = _cacheService.FindAnyCachedFilePath(song.Id);
            if (fallbackCached != null && File.Exists(fallbackCached))
            {
                var fi = new FileInfo(fallbackCached);
                var ext = Path.GetExtension(fallbackCached).TrimStart('.').ToLowerInvariant();
                source = new AudioSource
                {
                    SongId = song.Id,
                    Url = fallbackCached,
                    Format = ext,
                    Bitrate = 128,
                    Size = fi.Length,
                    Duration = song.Duration,
                    IsFromCache = true
                };
            }
        }

        if (source is null || string.IsNullOrWhiteSpace(source.Url))
        {
            if (lastException is not null)
                throw lastException;
            throw new BodianApiException("play/music/v2/audioUrl", 200, null, "该歌曲当前没有可用的播放地址", retryable: false);
        }

        if (!source.IsFromCache && !source.IsTrialClip(song.Duration) && _cacheService != null && EnableAudioCache && !string.IsNullOrWhiteSpace(source.Url))
        {
            var sId = song.Id;
            var sBr = resolvedQuality ?? PreferredQuality;
            var sUrl = source.Url;
            var sSize = source.Size;
            var limitMb = AudioCacheLimitMb;
            _ = Task.Run(async () =>
            {
                try
                {
                    var saved = await _cacheService.CacheAudioAsync(sId, sBr, sUrl, sSize, CancellationToken.None).ConfigureAwait(false);
                    if (saved != null)
                    {
                        await _cacheService.PruneIfNeededAsync(limitMb, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            });
        }

        if (generation != Volatile.Read(ref _generation)) return;

        var rightsAllowed = source.IsFromCache || await rightsTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _generation)) return;

        if (!_backend.IsRunning) await _backend.StartAsync(cancellationToken).ConfigureAwait(false);

        Publish(_snapshot with { State = PlaybackState.Loading, Generation = generation, AudioSource = source });
        SongStarted?.Invoke(this, song);

        await _backend.LoadAsync(source.Url, generation, song.Id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _backend.SetPausedAsync(false, cancellationToken).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _generation)) return;

        await lyricsTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _generation)) return;

        if (source.IsTrialClip(song.Duration))
        {
            var reason = rightsAllowed ? "服务端只给出了这段片段" : "当前账号的权益没有覆盖这首歌";
            Notice?.Invoke(this, $"《{song.Name}》只能试听 {source.Duration!.Value.TotalSeconds:0} 秒（{reason}）。");
        }

        Publish(_snapshot with
        {
            State = PlaybackState.Playing,
            IsPaused = false,
            Position = TimeSpan.Zero,
            EndReason = PlaybackEndReason.None,
            Error = null,
            AudioSource = source
        });
        StartObservation(generation, song.Id);
        return;
    }

    private static IReadOnlyList<string> GetQualityCandidates(string preferred)
    {
        return preferred switch
        {
            "2000kflac" => ["2000kflac", "320kmp3", "128kmp3"],
            "320kmp3" => ["320kmp3", "128kmp3"],
            "128kmp3" => ["128kmp3"],
            _ => ["320kmp3", "128kmp3"]
        };
    }

    private async Task<bool> SafeCheckRightAsync(long songId, CancellationToken cancellationToken)
    {
        try
        {
            return await _api.CheckPlayRightAsync(songId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private async Task FetchLyricsAsync(Song song, long generation, CancellationToken cancellationToken)
    {
        try
        {
            using var lyricsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lyricsCts.CancelAfter(_timeouts.Lyrics);
            var content = await _api.GetLyricsAsync(song.Id, lyricsCts.Token).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation)) return;
            Lyrics = LyricParser.Parse(content);
            LyricsChanged?.Invoke(this, new LyricsChangedEventArgs(Lyrics, song.Id, generation));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            Lyrics = LyricDocument.Empty;
            LyricsChanged?.Invoke(this, new LyricsChangedEventArgs(Lyrics, song.Id, generation));
        }
    }

    private void StartObservation(long generation, long songId)
    {
        var cts = new CancellationTokenSource();
        _observeCts = cts;
        _observeTask = Task.Run(() => ObserveLoopAsync(generation, songId, cts.Token));
    }

    private async Task ObserveLoopAsync(long generation, long songId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in _backend.ObserveAsync(generation, songId, cancellationToken).ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (generation != Volatile.Read(ref _generation)) return;
                Apply(update);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            Publish(_snapshot with { State = PlaybackState.Failed, Error = exception.Message });
            Notice?.Invoke(this, $"播放后端异常：{exception.Message}");
        }
    }

    private void Apply(PlaybackSnapshot update)
    {
        var state = update.State;
        if (state == PlaybackState.Playing)
        {
            state = update.IsSeeking ? PlaybackState.Seeking
                : update.IsPaused ? PlaybackState.Paused
                : PlaybackState.Playing;
        }

        _volume = update.Volume;
        var merged = _snapshot with
        {
            Generation = update.Generation != 0 ? update.Generation : _snapshot.Generation,
            SongId = update.SongId ?? _snapshot.SongId,
            Position = update.Position,
            Duration = update.Duration > TimeSpan.Zero ? update.Duration : _snapshot.Duration,
            IsPaused = update.IsPaused,
            IsSeeking = update.IsSeeking,
            Volume = update.Volume,
            State = state,
            EndReason = update.EndReason,
            Error = update.Error
        };
        if (update.EndReason != PlaybackEndReason.None) merged = merged with { State = PlaybackState.Ended };
        Publish(merged);

        if (update.EndReason == PlaybackEndReason.EndOfFile) _ = OnEndOfFileAsync();
        else if (update.EndReason == PlaybackEndReason.Error && RetryOnFailure &&
                 Interlocked.Exchange(ref _remainingRecovery, 0) > 0)
        {
            var failedGeneration = update.Generation;
            _ = Task.Run(async () =>
            {
                if (failedGeneration != Volatile.Read(ref _generation)) return;
                try { await PlayCurrentAsync(CancellationToken.None, retryBudget: 0, expectedGeneration: failedGeneration).ConfigureAwait(false); }
                catch (Exception) {  }
            });
        }
    }

    private async Task OnEndOfFileAsync()
    {
        if (_autoAdvancing) return;
        _autoAdvancing = true;
        try
        {
            if (PlayMode == PlaybackMode.RepeatOne)
            {
                await PlayCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            await MoveAsync(+1, userInitiated: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            _autoAdvancing = false;
        }
    }

    private async Task MoveAsync(int direction, bool userInitiated, CancellationToken cancellationToken)
    {
        if (userInitiated) CancelRecommendation();
        if (direction > 0 && _recommendationRadio)
        {
            await MoveRecommendationAsync(userInitiated, cancellationToken).ConfigureAwait(false);
            return;
        }
        var reachedEnd = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_queue.Count == 0) return;

            int target;
            if (_recommendationRadio)
            {
                target = Math.Max(0, _queueIndex - 1);
            }
            else if (PlayMode == PlaybackMode.Shuffle)
            {
                if (_queue.Count == 1)
                {
                    target = 0;
                }
                else if (direction > 0)
                {
                    if (_queueIndex >= 0 && _queueIndex < _queue.Count)
                    {
                        _shuffleHistory.Add(_queueIndex);
                        if (_shuffleHistory.Count > 100)
                        {
                            _shuffleHistory.RemoveAt(0);
                        }
                    }
                    var next = Random.Shared.Next(0, _queue.Count - 1);
                    if (_queueIndex >= 0 && next >= _queueIndex)
                    {
                        next++;
                    }
                    target = next;
                }
                else
                {
                    if (_shuffleHistory.Count > 0)
                    {
                        target = _shuffleHistory[^1];
                        _shuffleHistory.RemoveAt(_shuffleHistory.Count - 1);
                        if (target >= _queue.Count) target = 0;
                    }
                    else
                    {
                        var prev = Random.Shared.Next(0, _queue.Count - 1);
                        if (_queueIndex >= 0 && prev >= _queueIndex)
                        {
                            prev++;
                        }
                        target = prev;
                    }
                }
            }
            else if (PlayMode == PlaybackMode.RepeatOne)
            {
                if (!userInitiated)
                {
                    target = _queueIndex >= 0 && _queueIndex < _queue.Count ? _queueIndex : 0;
                }
                else
                {
                    target = direction > 0
                        ? (_queueIndex + 1) % _queue.Count
                        : (_queueIndex - 1 + _queue.Count) % _queue.Count;
                }
            }
            else if (PlayMode == PlaybackMode.Sequential)
            {
                target = _queueIndex + direction;
                if (target >= _queue.Count)
                {
                    reachedEnd = true;
                    target = _queueIndex;
                }
                else if (target < 0)
                {
                    target = 0;
                }
            }
            else
            {
                target = direction > 0
                    ? (_queueIndex + 1) % _queue.Count
                    : (_queueIndex - 1 + _queue.Count) % _queue.Count;
            }

            _queueIndex = target;
        }
        finally
        {
            _gate.Release();
        }

        if (reachedEnd)
        {
            Interlocked.Increment(ref _generation);
            await CancelObservationAsync().ConfigureAwait(false);
            if (_backend.IsRunning)
            {
                try { await _backend.StopMediaAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception) {  }
            }
            Publish(_snapshot with { State = PlaybackState.Ended, Position = TimeSpan.Zero, IsPaused = false, EndReason = PlaybackEndReason.EndOfFile });
            return;
        }

        await PlayCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MoveRecommendationAsync(bool userInitiated, CancellationToken cancellationToken)
    {
        CancellationTokenSource request;
        long expectedGeneration;
        lock (_requestSync)
        {
            if (_shuttingDown) return;
            _recommendationCts?.Cancel();
            request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _recommendationCts = request;
            expectedGeneration = _generation;
        }
        try
        {
            HashSet<long> recentIds;
            long? currentId;
            await _gate.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                if (!_recommendationRadio || _queue.Count == 0 || (!userInitiated && _snapshot.State != PlaybackState.Ended)) return;
                recentIds = _queue.TakeLast(50).Select(song => song.Id).ToHashSet();
                currentId = CurrentSong?.Id;
            }
            finally { _gate.Release(); }

            Song? recommendation;
            try { recommendation = await FetchRecommendationAsync(recentIds, currentId, request.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { recommendation = null; }
            request.Token.ThrowIfCancellationRequested();

            await _gate.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                lock (_requestSync)
                {
                    if (request.IsCancellationRequested || _shuttingDown || expectedGeneration != _generation || !_recommendationRadio) return;
                    if (recommendation is not null)
                    {
                        if (_queueIndex < _queue.Count - 1)
                            _queue.RemoveRange(_queueIndex + 1, _queue.Count - _queueIndex - 1);
                        _queue.Add(recommendation);
                        if (_queue.Count > 100)
                        {
                            _queue.RemoveAt(0);
                            _queueIndex--;
                        }
                        _queueIndex = _queue.Count - 1;
                    }
                }
            }
            finally { _gate.Release(); }

            if (recommendation is null)
            {
                Notice?.Invoke(this, "暂时没有获取到新的推荐歌曲，请稍后再试。");
                return;
            }
            QueueChanged?.Invoke(this, EventArgs.Empty);
            await PlayCurrentAsync(request.Token, expectedGeneration: expectedGeneration).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            lock (_requestSync)
            {
                if (ReferenceEquals(_recommendationCts, request)) _recommendationCts = null;
                request.Dispose();
            }
        }
    }

    private async Task CancelObservationAsync()
    {
        var cts = _observeCts;
        var task = _observeTask;
        _observeCts = null;
        _observeTask = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) {  }
        }
        cts.Dispose();
    }

    private void Publish(PlaybackSnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }
}
