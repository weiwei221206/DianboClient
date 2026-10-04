using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dianbo.Core.Models;
using Dianbo.Core.Playback;
using Dianbo.Core.Services;
using Dianbo.Infrastructure.Api;
using Dianbo.Infrastructure.Playback;
using Dianbo.Infrastructure.Storage;
using Dianbo.Infrastructure.Auth;

namespace Dianbo.Tests;

public static class ReviewRegressionChecks
{
    public static async Task<List<(string Name, string? Error)>> RunAsync()
    {
        var results = new List<(string, string?)>();
        async Task Test(string name, Func<Task> action)
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(12)); results.Add((name, null)); }
            catch (Exception error) { results.Add((name, error.ToString())); }
        }
        await Test("相对路径与凭据字段脱敏", () =>
        {
            var text = RedactedLog.Sanitize("auth path=ucenter/users/pub/123?token=SECRET&uid=123 qrCode=QRTICKET http=200");
            Require(!text.Contains("SECRET") && !text.Contains("QRTICKET") && !text.Contains("123"), text);
            return Task.CompletedTask;
        });
        await Test("停止后迟到的取地址结果不能启动播放", StopDuringLoad);
        await Test("迟到的推荐结果不能覆盖停止或新队列", LateRecommendation);
        await Test("推荐下一首迟到后不能恢复播放", LateRecommendationNext);
        await Test("旧版试听缓存升级时失效", LegacyTrialCacheInvalidation);
        await Test("收藏失败操作重启后保留并补发", FavoritePendingRestart);
        await Test("试听片段不写入普通音频缓存", TrialClipNotCached);
        await Test("单曲循环与列表循环跳过均有界", BoundedSkip);
        await Test("加载失败仅重试一次且开关有效", Retry);
        await Test("播放期间报错只重试一次", ObserveRetry);
        await Test("IPC并发写入保持完整JSON与请求关联", ConcurrentIpc);
        await Test("IPC写入阻塞受命令期限约束", BlockedIpc);
        await Test("收藏按账号隔离并支持重新登录恢复", AccountIsolation);
        await Test("切换账号取消旧收藏请求且新请求使用新凭据", AccountInFlight);
        await Test("迁移在重启时采集最新完整数据", Migration);
        await Test("离线播放本地缓存歌曲无需网络鉴权", OfflinePlaybackCachedSong);
        await Test("离线启动保留已保存会话不降级为访客", OfflineAuthSessionPreserved);
        await Test("离线启动网络无响应超时保留会话不降级", OfflineAuthTimeoutSessionPreserved);
        await Test("首选音质解析超时仍能回退已缓存其他音质", PreferredQualityTimeoutFallbackToCache);
        await Test("音频流下载超时释放锁并不占用后续下载", AudioDownloadTimeoutReleasesInFlight);
        await Test("音频下载立即失败后恢复网络可重新缓存", AudioDownloadImmediateFailureRetry);
        await Test("同一音频并发缓存只发起一次下载", AudioDownloadConcurrentRequests);
        results.AddRange(await Round2Checks.RunAsync());
        return results;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Song Song(long id) => new() { Id = id, Name = "test", Artist = "test" };
    private static IBodianApiClient Api(Action<ApiProxy>? configure = null)
    {
        var api = DispatchProxy.Create<IBodianApiClient, ApiProxy>();
        configure?.Invoke((ApiProxy)api);
        return api;
    }
    public class ApiProxy : DispatchProxy
    {
        public Func<long, CancellationToken, Task<AudioSource>> Resolve = (id, _) => Task.FromResult(new AudioSource { SongId = id, Url = "test-audio" });
        public Func<long, CancellationToken, Task<bool>> CheckRight = (_, _) => Task.FromResult(true);
        public Func<CancellationToken, Task<IReadOnlyList<Song>>> Recommend = _ => Task.FromResult<IReadOnlyList<Song>>([]);
        public Func<CancellationToken, Task<Playlist?>> Fond = _ => Task.FromResult<Playlist?>(null);
        public Func<long, CancellationToken, Task<IReadOnlyList<Song>>> PlaylistSongs = (_, _) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Func<long, CancellationToken, Task<bool>> AddFavorite = (_, _) => Task.FromResult(true);
        public Func<long, CancellationToken, Task<bool>> RemoveFavorite = (_, _) => Task.FromResult(true);
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "ResolveAudioSourceAsync" => Resolve((long)args![0]!, (CancellationToken)args[^1]!),
            "CheckPlayRightAsync" => CheckRight((long)args![0]!, (CancellationToken)args[^1]!),
            "GetRecommendSongsAsync" => Recommend((CancellationToken)args![^1]!),
            "GetLyricsAsync" => Task.FromResult(""),
            "GetFondPlaylistAsync" => Fond((CancellationToken)args![^1]!),
            "GetAllPlaylistSongsAsync" => PlaylistSongs((long)args![0]!, (CancellationToken)args[^2]!),
            "AddPlaylistMusicAsync" => AddFavorite((long)args![1]!, (CancellationToken)args[^1]!),
            "DeletePlaylistMusicAsync" => RemoveFavorite((long)args![1]!, (CancellationToken)args[^1]!),
            "RecordBehaviorAsync" => Task.FromResult(true),
            _ => throw new NotSupportedException(method.Name)
        };
    }
    private static PlaybackCoordinator Coordinator(IBodianApiClient api, Backend backend) => new(api, backend) { PreferredQuality = "128kmp3", EnableAudioCache = false };

    private static async Task StopDuringLoad()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend();
        var coordinator = Coordinator(Api(p => p.Resolve = async (id, _) =>
        {
            entered.TrySetResult(); await release.Task;
            return new AudioSource { SongId = id, Url = "late" };
        }), backend);
        var play = coordinator.PlayQueueAsync([Song(1)], 0, default);
        await entered.Task;
        var stop = coordinator.StopAsync(default);
        release.SetResult();
        await Task.WhenAll(play, stop);
        Require(backend.Loads == 0 && coordinator.Snapshot.State == PlaybackState.Idle, "停止后仍然加载或没有回到Idle");
        await coordinator.ShutdownAsync();
    }

    private static async Task LateRecommendation()
    {
        foreach (var action in new[] { "stop", "clear", "replace" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new Backend();
            var coordinator = Coordinator(Api(p => p.Recommend = async _ =>
            {
                entered.TrySetResult();
                await release.Task; // 模拟不理会取消信号的网络响应
                return [Song(99)];
            }), backend);
            var recommendation = coordinator.StartRecommendationRadioAsync(default);
            await entered.Task;
            switch (action)
            {
                case "stop": await coordinator.StopAsync(default); break;
                case "clear": await coordinator.ClearQueueAsync(default); break;
                default: await coordinator.PlayQueueAsync([Song(2)], 0, default); break;
            }
            release.SetResult();
            await recommendation;
            Require(coordinator.CurrentSong?.Id == (action == "replace" ? 2 : null)
                && !coordinator.IsRecommendationRadio
                && backend.Loads == (action == "replace" ? 1 : 0), $"{action}: 迟到推荐覆盖了操作");
            await coordinator.ShutdownAsync();
        }
    }

    private static async Task LateRecommendationNext()
    {
        foreach (var naturalEnd in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var backend = new Backend { EmitEndOfFile = naturalEnd };
            var coordinator = Coordinator(Api(p => p.Recommend = async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) return [Song(1)];
                entered.TrySetResult();
                await release.Task; // 网络忽略取消后仍返回新歌
                return [Song(2)];
            }), backend);
            await coordinator.StartRecommendationRadioAsync(default);
            Task? next = naturalEnd ? null : coordinator.NextAsync(default);
            await entered.Task;
            await coordinator.StopAsync(default);
            release.SetResult();
            if (next is not null) await next;
            else await Task.Delay(100);
            Require(coordinator.Snapshot.State == PlaybackState.Idle && coordinator.CurrentSong?.Id == 1 && backend.Loads == 1,
                naturalEnd ? "自然结束后的迟到推荐重新播放" : "手动下一首的迟到推荐重新播放");
            await coordinator.ShutdownAsync();
        }
    }

    private static Task LegacyTrialCacheInvalidation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dianbo-legacy-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var oldPath = Path.Combine(dir, "42_128kmp3.mp3");
            File.WriteAllBytes(oldPath, new byte[256]);
            var cache = new AudioCacheService(dir);
            Require(cache.GetCachedFilePath(42, "128kmp3") is null && !File.Exists(oldPath), "升级前片段仍可命中缓存");
            var currentPath = Path.Combine(dir, "43_128kmp3.mp3");
            File.WriteAllBytes(currentPath, new byte[256]);
            var reopened = new AudioCacheService(dir);
            Require(reopened.GetCachedFilePath(43, "128kmp3") == currentPath, "新格式缓存被重复清理");
            reopened.ClearCacheAsync().GetAwaiter().GetResult();
            Require(reopened.GetCacheSizeAsync().GetAwaiter().GetResult().FileCount == 0, "版本标记被计为音频缓存");
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    private static async Task FavoritePendingRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "dianbo-favorite-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var server = new Dictionary<long, Song> { [2] = Song(2) };
            var failWrites = true;
            var addCalls = 0;
            var removeCalls = 0;
            var api = Api(p =>
            {
                p.Fond = _ => Task.FromResult<Playlist?>(new Playlist { Id = 7, Name = "收藏" });
                p.PlaylistSongs = (_, _) => Task.FromResult<IReadOnlyList<Song>>(server.Values.ToList());
                p.AddFavorite = (id, _) =>
                {
                    addCalls++;
                    if (failWrites) return Task.FromResult(false);
                    server[id] = Song(id);
                    return Task.FromResult(true);
                };
                p.RemoveFavorite = (id, _) =>
                {
                    removeCalls++;
                    if (failWrites) return Task.FromResult(false);
                    server.Remove(id);
                    return Task.FromResult(true);
                };
            });
            var path = Path.Combine(root, "favorites.json");
            var credentials = new Credentials();
            var first = new FavoriteService(path, api, credentials);
            await first.AddFavoriteAsync(Song(1));
            await first.RemoveFavoriteAsync(2);
            var restarted = new FavoriteService(path, api, credentials);
            Require(restarted.IsFavorite(1) && !restarted.IsFavorite(2), "重启后本地收藏状态错误");
            await restarted.SyncAsync();
            Require(restarted.IsFavorite(1) && !restarted.IsFavorite(2), "失败的补发冲掉本地修改");
            failWrites = false;
            await restarted.SyncAsync();
            Require(addCalls >= 2 && removeCalls >= 2 && server.ContainsKey(1) && !server.ContainsKey(2), "刷新未补发待同步操作");
            var again = new FavoriteService(path, api, credentials);
            await again.SyncAsync();
            Require(again.IsFavorite(1) && !again.IsFavorite(2), "补发成功后重启状态错误");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CountingCache : IAudioCacheService
    {
        public int Writes;
        public string CacheDirectory => Path.GetTempPath();
        public void SetCacheDirectory(string path) { }
        public string? GetCachedFilePath(long id, string bitrate) => null;
        public string? FindAnyCachedFilePath(long id) => null;
        public Task<string?> CacheAudioAsync(long id, string bitrate, string url, long? size, CancellationToken token = default)
        { Interlocked.Increment(ref Writes); return Task.FromResult<string?>(null); }
        public Task<CacheSizeInfo> GetCacheSizeAsync(CancellationToken token = default) => Task.FromResult(new CacheSizeInfo(0, 0));
        public Task ClearCacheAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task PruneIfNeededAsync(int limit, CancellationToken token = default) => Task.CompletedTask;
    }

    private static async Task TrialClipNotCached()
    {
        var cache = new CountingCache();
        var api = Api(p => p.Resolve = (id, _) => Task.FromResult(new AudioSource
        { SongId = id, Url = "trial", Duration = TimeSpan.FromSeconds(30) }));
        var coordinator = new PlaybackCoordinator(api, new Backend(), cache) { PreferredQuality = "128kmp3" };
        await coordinator.PlayQueueAsync([Song(1) with { Duration = TimeSpan.FromMinutes(4) }], 0, default);
        Require(cache.Writes == 0, "试听片段进入了普通音频缓存");
        await coordinator.ShutdownAsync();
    }

    private static async Task BoundedSkip()
    {
        foreach (var mode in new[] { PlaybackMode.RepeatOne, PlaybackMode.RepeatAll, PlaybackMode.Shuffle })
        {
            int calls = 0;
            var coordinator = Coordinator(Api(p => p.Resolve = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromException<AudioSource>(new BodianApiException("test", 403, 403, "denied", false));
            }), new Backend());
            coordinator.SkipUnplayable = true; coordinator.PlayMode = mode;
            await coordinator.PlayQueueAsync([Song(1), Song(2)], 0, default);
            Require(calls == 2 && coordinator.Snapshot.State == PlaybackState.Failed, $"{mode}: calls={calls}");
            await coordinator.ShutdownAsync();
        }
    }

    private static async Task Retry()
    {
        foreach (bool enabled in new[] { false, true })
        {
            var backend = new Backend { FailLoads = true };
            var coordinator = Coordinator(Api(), backend); coordinator.RetryOnFailure = enabled;
            await coordinator.PlayQueueAsync([Song(1)], 0, default);
            Require(backend.Loads == (enabled ? 2 : 1), $"retry={enabled}, loads={backend.Loads}");
            await coordinator.ShutdownAsync();
        }
        var recoveryBackend = new Backend { FailFirstLoad = true };
        var recovery = Coordinator(Api(), recoveryBackend);
        await recovery.PlayQueueAsync([Song(1)], 0, default);
        Require(recoveryBackend.Loads == 2 && recovery.Snapshot.State == PlaybackState.Playing, "重试成功未恢复Playing");
        await recovery.ShutdownAsync();
    }

    private static async Task ObserveRetry()
    {
        var backend = new Backend { EmitError = true };
        var coordinator = Coordinator(Api(), backend);
        await coordinator.PlayQueueAsync([Song(1)], 0, default);
        for (int i = 0; i < 100 && backend.Loads < 2; i++) await Task.Delay(10);
        await Task.Delay(100);
        Require(backend.Loads == 2, $"异步错误重试次数错误：{backend.Loads}");
        await coordinator.ShutdownAsync();
    }

    private static async Task ConcurrentIpc()
    {
        string name = "dianbo-review-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var ipc = new MpvIpcConnection(name, TimeSpan.FromSeconds(5));
        var accept = server.WaitForConnectionAsync();
        await ipc.ConnectAsync(TimeSpan.FromSeconds(2), default); await accept;
        var serve = Task.Run(async () =>
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            for (int i = 0; i < 24; i++)
            {
                using var doc = JsonDocument.Parse((await reader.ReadLineAsync())!);
                var root = doc.RootElement;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { request_id = root.GetProperty("request_id").GetInt64(), data = root.GetProperty("command")[1].GetInt32(), error = "success" }));
            }
        });
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
        {
            var result = await ipc.SendAsync(default, "echo", i, new string('x', 16000));
            Require(result["data"]!.GetValue<int>() == i, "命令应答串线");
        }));
        await serve;
    }

    private static async Task BlockedIpc()
    {
        string name = "dianbo-review-block-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024);
        await using var ipc = new MpvIpcConnection(name, TimeSpan.FromMilliseconds(150));
        var accept = server.WaitForConnectionAsync();
        await ipc.ConnectAsync(TimeSpan.FromSeconds(2), default); await accept;
        var started = Environment.TickCount64;
        try { await ipc.SendAsync(default, "echo", new string('x', 2_000_000)); throw new Exception("阻塞写入未超时"); }
        catch (MpvIpcException) { Require(Environment.TickCount64 - started < 2000, "写入期限未生效"); }
    }

    private sealed class Credentials : IAuthCredentialSource
    {
        public Session? Session { get; set; }
        public string DevId => "test-device";
    }
    private static async Task AccountIsolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "dianbo-review-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var credentials = new Credentials { Session = new Session { Uid = 1, Token = "test-a", DevId = "test-device" } };
            var service = new FavoriteService(Path.Combine(root, "favorites.json"), Api(), credentials);
            await service.AddFavoriteAsync(Song(11));
            credentials.Session = credentials.Session with { Uid = 2, Token = "test-b" };
            Require(!service.IsFavorite(11), "账号B读取了账号A的收藏");
            await service.AddFavoriteAsync(Song(22));
            credentials.Session = null;
            Require(!service.IsFavorite(11) && !service.IsFavorite(22), "游客读取了登录账号收藏");
            credentials.Session = new Session { Uid = 1, Token = "test-a2", DevId = "test-device" };
            Require(service.IsFavorite(11) && !service.IsFavorite(22), "重新登录账号A没有恢复独立收藏");
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task Migration()
    {
        var root = Path.Combine(Path.GetTempPath(), "dianbo-review-migration-" + Guid.NewGuid().ToString("N"));
        var original = Environment.GetEnvironmentVariable("DIANBO_CONFIG_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("DIANBO_CONFIG_DIRECTORY", Path.Combine(root, "config"));
            var old = new AppPaths(Path.Combine(root, "old")); Directory.CreateDirectory(old.Root);
            File.WriteAllText(old.HistoryPath, "before");
            var target = Path.Combine(root, "new");
            DataRootMigration.Schedule(old, target);
            Require(!File.Exists(Path.Combine(target, "history.json")), "当前进程不应立即迁移");
            File.WriteAllText(old.HistoryPath, "after");
            Directory.CreateDirectory(Path.Combine(old.Root, "accounts", "1"));
            File.WriteAllText(Path.Combine(old.Root, "accounts", "1", "favorites.json"), "account");
            var migrated = DataRootMigration.ApplyPending(old);
            Require(migrated.Root == target && File.ReadAllText(migrated.HistoryPath) == "after", "没有迁移退出前的最新数据");
            Require(File.Exists(Path.Combine(target, "accounts", "1", "favorites.json")), "账号数据未迁移");
            Require(File.Exists(old.HistoryPath), "不应删除原文件");
            return Task.CompletedTask;
        }
        finally { Environment.SetEnvironmentVariable("DIANBO_CONFIG_DIRECTORY", original); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class AccountHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var query = request.RequestUri!.Query;
            lock (Requests) Requests.Add(query);
            if (query.Contains("token=test-a&") || query.EndsWith("token=test-a"))
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":200,\"data\":{\"id\":77,\"name\":\"test\"}}", Encoding.UTF8, "application/json")
            };
        }
    }

    private static async Task AccountInFlight()
    {
        var root = Path.Combine(Path.GetTempPath(), "dianbo-review-flight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var credentials = new Credentials { Session = new Session { Uid = 1, Token = "test-a", DevId = "test-device" } };
            using var handler = new AccountHandler();
            using var http = new HttpClient(handler);
            var service = new FavoriteService(Path.Combine(root, "favorites.json"), new BodianApiClient(http, credentials), credentials);
            var oldRequest = service.AddFavoriteAsync(Song(11));
            await handler.Started.Task;
            credentials.Session = credentials.Session with { Uid = 2, Token = "test-b" };
            Require(!service.IsFavorite(11), "切换后旧收藏仍可见");
            try { await oldRequest; throw new Exception("旧任务未取消"); }
            catch (OperationCanceledException) { }
            await service.AddFavoriteAsync(Song(22));
            lock (handler.Requests)
                Require(handler.Requests.Skip(1).All(q => !q.Contains("token=test-a")) && handler.Requests.Any(q => q.Contains("token=test-b")), "请求使用了错误账号凭据：" + string.Join(" | ", handler.Requests));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Backend : IPlaybackBackend
    {
        public int Loads;
        public bool FailLoads, FailFirstLoad, EmitError, EmitEndOfFile;
        public bool IsRunning { get; private set; }
        public bool IsConfigured => true;
        public string? LastError => null;
        public event EventHandler<BackendExitInfo>? Exited { add { } remove { } }
        public Task StartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { IsRunning = false; return Task.CompletedTask; }
        public Task LoadAsync(string url, long generation, long songId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Loads);
            if (FailLoads || (FailFirstLoad && Loads == 1)) throw new IOException("test load failed");
            return Task.CompletedTask;
        }
        public Task SetPausedAsync(bool paused, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken token) => Task.CompletedTask;
        public Task StopMediaAsync(CancellationToken token) => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken token) => Task.CompletedTask;
        public async IAsyncEnumerable<PlaybackSnapshot> ObserveAsync(long generation, long songId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await Task.Delay(10, token);
            if (EmitError) yield return new PlaybackSnapshot { Generation = generation, SongId = songId, State = PlaybackState.Failed, EndReason = PlaybackEndReason.Error };
            if (EmitEndOfFile) yield return new PlaybackSnapshot { Generation = generation, SongId = songId, State = PlaybackState.Ended, EndReason = PlaybackEndReason.EndOfFile };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task OfflinePlaybackCachedSong()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dianbo-test-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cache = new AudioCacheService(tempDir);
            var cachePath = Path.Combine(tempDir, "888_128kmp3.flac");
            File.WriteAllBytes(cachePath, new byte[256]);

            var backend = new Backend();
            var coordinator = new PlaybackCoordinator(Api(p =>
            {
                p.Resolve = (_, _) => Task.FromException<AudioSource>(new HttpRequestException("network unreachable"));
                p.CheckRight = (_, _) => Task.FromException<bool>(new HttpRequestException("network unreachable"));
            }), backend, cache)
            {
                PreferredQuality = "128kmp3",
                EnableAudioCache = true
            };

            await coordinator.PlayQueueAsync([Song(888)], 0, default);
            Require(backend.Loads == 1, $"应该直接播放本地缓存，实际加载次数: {backend.Loads}");
            Require(coordinator.Snapshot.State == PlaybackState.Playing, $"播放状态应为 Playing，实际为: {coordinator.Snapshot.State}");
            Require(coordinator.Snapshot.SongId == 888, "播放歌曲ID应为888");
            await coordinator.ShutdownAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private sealed class OfflineAuthHandler : HttpMessageHandler
    {
        public bool SimulateNetworkFailure = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (SimulateNetworkFailure)
            {
                throw new HttpRequestException("Simulated network down / DNS failure");
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"code\":401,\"msg\":\"token expired\"}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MemorySessionStore : ISessionStore
    {
        public string Location => "memory";
        public Session? Current;
        public Task<Session?> LoadAsync(CancellationToken token) => Task.FromResult(Current);
        public Task SaveAsync(Session session, CancellationToken token) { Current = session; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken token) { Current = null; return Task.CompletedTask; }
    }

    private sealed class DummyDeviceIdentity : IDeviceIdentity
    {
        public string DevId => "dummy-dev-id";
        public void EnsurePersisted() { }
    }

    private static async Task OfflineAuthSessionPreserved()
    {
        var store = new MemorySessionStore { Current = new Session { Uid = 456, Token = "saved-token", DevId = "dev-1" } };
        var handler = new OfflineAuthHandler { SimulateNetworkFailure = true };
        using var client = new HttpClient(handler);
        var authService = new AuthService(client, store, new DummyDeviceIdentity());

        var restored = await authService.RestoreAsync(default);
        Require(restored != null, "离线时应成功恢复已保存会话");
        Require(authService.IsSignedIn, "离线时应保持登录状态");
        Require(authService.Current?.Uid == 456, "离线会话 UID 错误");
        Require(authService.Profile?.Nickname?.Contains("离线") == true, "离线未标记离线");
        Require(store.Current != null, "离线异常不应清空凭据存储");

        // 当网络恢复且服务端明确拒绝（401）时，才应清空并退出登录
        handler.SimulateNetworkFailure = false;
        var rejected = await authService.RestoreAsync(default);
        Require(rejected == null, "被服务端拒绝时应返回 null");
        Require(!authService.IsSignedIn, "被服务端拒绝时应退出登录");
        Require(store.Current == null, "被服务端拒绝时应清空凭据存储");
    }

    private sealed class TimeoutAuthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 模拟 HttpClient.Timeout 触发抛出的 TaskCanceledException
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.");
        }
    }

    private static async Task OfflineAuthTimeoutSessionPreserved()
    {
        var store = new MemorySessionStore { Current = new Session { Uid = 789, Token = "timeout-token", DevId = "dev-timeout" } };
        using var handler = new TimeoutAuthHandler();
        using var client = new HttpClient(handler);
        var authService = new AuthService(client, store, new DummyDeviceIdentity());

        // 当网络调用发生超时（TaskCanceledException 且非主动调用 cancellationToken 取消）时，应进入离线兜底
        var restored = await authService.RestoreAsync(default);
        Require(restored != null, "网络无响应超时应成功恢复离线会话");
        Require(authService.IsSignedIn, "超时后应保持已登录状态");
        Require(authService.Current?.Uid == 789, "会话 UID 错误");
        Require(authService.Profile?.Nickname?.Contains("离线") == true, "资料应标记为离线");
        Require(store.Current != null, "超时不应清空凭据存储");

        // 当调用方主动传入已取消的 token 时，应当正确抛出 OperationCanceledException
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        try
        {
            await authService.RestoreAsync(callerCts.Token);
            throw new Exception("调用方主动取消时未抛出异常");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task PreferredQualityTimeoutFallbackToCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dianbo-test-cache-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var cache = new AudioCacheService(tempDir);
            // 假设本地仅缓存了无损音质（flac）
            var cachePath = Path.Combine(tempDir, "999_flac.flac");
            File.WriteAllBytes(cachePath, new byte[256]);

            var backend = new Backend();
            var coordinator = new PlaybackCoordinator(Api(p =>
            {
                // 首选普通音质（128kmp3）在线解析时超时无响应
                p.Resolve = (id, ct) => Task.FromException<AudioSource>(new TaskCanceledException("HttpClient timeout"));
                p.CheckRight = (_, _) => Task.FromResult(true);
            }), backend, cache)
            {
                PreferredQuality = "128kmp3", // 首选普通音质（本地未命中）
                EnableAudioCache = true
            };

            await coordinator.PlayQueueAsync([Song(999)], 0, default);
            Require(backend.Loads == 1, $"首选音质超时后应回退播放其他音质的本地缓存，实际加载次数: {backend.Loads}");
            Require(coordinator.Snapshot.State == PlaybackState.Playing, $"播放状态应为 Playing，实际为: {coordinator.Snapshot.State}");
            Require(coordinator.Snapshot.SongId == 999, "播放歌曲ID应为999");
            await coordinator.ShutdownAsync();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private sealed class HangingStreamHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingStream())
            };
            return Task.FromResult(response);
        }

        private sealed class HangingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 1024 * 1024;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                // 模拟读取中途连接挂起，等待单次读取超时取消
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }

    private static async Task AudioDownloadTimeoutReleasesInFlight()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dianbo-test-cache-hang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var handler = new HangingStreamHandler();
            using var client = new HttpClient(handler);
            var cache = new AudioCacheService(tempDir, client)
            {
                ReadInactivityTimeout = TimeSpan.FromMilliseconds(50) // 单次数据流读取超时设置为 50ms
            };

            // 首次下载：流挂起，50ms 后触发 ReadInactivityTimeout 抛出异常并清理
            var result = await cache.CacheAudioAsync(12345, "128kmp3", "http://test/audio.mp3", null, default);
            Require(result == null, "下载流挂起超时应返回 null");

            // 验证未遗留 .tmp 临时文件
            var tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
            Require(tmpFiles.Length == 0, $"超时后临时文件未删除，残留数量: {tmpFiles.Length}");

            // 验证 _inFlightDownloads 锁已释放：再次调用不会被旧任务永久卡死
            var secondTask = cache.CacheAudioAsync(12345, "128kmp3", "http://test/audio.mp3", null, default);
            var completed = await Task.WhenAny(secondTask, Task.Delay(1000));
            Require(completed == secondTask, "下载锁未释放，导致后续下载调用被永久挂起");
            Require(await secondTask == null, "第二次模拟超时应返回 null");
            Require(handler.Calls == 2, "第二次调用必须重新发起 HTTP 请求");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
    private sealed class RecoveringAudioHandler : HttpMessageHandler
    {
        public int Calls;
        public bool FailImmediately;
        public TaskCompletionSource? Release;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (FailImmediately) throw new HttpRequestException("模拟断网");
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            var bytes = new byte[2048];
            Encoding.ASCII.GetBytes("ID3").CopyTo(bytes, 0);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }

    private static async Task AudioDownloadImmediateFailureRetry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dianbo-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new RecoveringAudioHandler { FailImmediately = true };
            using var client = new HttpClient(handler);
            var cache = new AudioCacheService(dir, client);
            Require(await cache.CacheAudioAsync(42, "128kmp3", "http://test/audio", null) == null, "断网应失败");
            handler.FailImmediately = false;
            var result = await cache.CacheAudioAsync(42, "128kmp3", "http://test/audio", null);
            Require(handler.Calls == 2, "恢复网络后未重新请求");
            Require(result is not null && File.Exists(result), "恢复网络后未生成缓存");
            Require(Directory.GetFiles(dir, "*.tmp").Length == 0, "存在临时文件残留");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static async Task AudioDownloadConcurrentRequests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dianbo-concurrent-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new RecoveringAudioHandler
            {
                Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            using var client = new HttpClient(handler);
            var cache = new AudioCacheService(dir, client);
            var calls = new Task<string?>[32];
            Parallel.For(0, calls.Length, i => calls[i] = cache.CacheAudioAsync(43, "128kmp3", "http://test/audio", null));
            var requestCount = handler.Calls;
            handler.Release.SetResult();
            var results = await Task.WhenAll(calls);
            Require(requestCount == 1 && handler.Calls == 1, $"并发请求重复下载：{handler.Calls}");
            Require(results.All(path => path is not null && path == results[0] && File.Exists(path)), "等待者没有共享成功结果");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
