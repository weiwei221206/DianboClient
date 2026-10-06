using System.Net.Http.Headers;
using Dianbo.Core.Models;
using Dianbo.Core.Playback;
using Dianbo.Core.Services;
using Dianbo.Infrastructure.Api;
using Dianbo.Infrastructure.Auth;
using Dianbo.Infrastructure.Playback;
using Dianbo.Infrastructure.Storage;
using Microsoft.UI.Dispatching;

namespace Dianbo.App.Services;

/// <summary>应用组装入口：所有服务在这里创建，退出时统一释放。</summary>
public sealed class AppServices : IDisposable
{
    private readonly MpvOptions _mpvOptions;

    private AppServices(
        AppPaths paths,
        RedactedLog log,
        SettingsStore settingsStore,
        AppSettings settings,
        DeviceIdentity identity,
        HttpClient http,
        DpapiSessionStore sessionStore,
        AuthService auth,
        BodianApiClient api,
        ZxingQrCodeRenderer qrRenderer,
        MpvOptions mpvOptions,
        MpvPlaybackBackend backend,
        PlaybackCoordinator coordinator,
        HistoryService history,
        FavoriteService favorites,
        UserPlaylistService playlists,
        QueueStateStore queueStore,
        IAudioCacheService audioCache)
    {
        Paths = paths;
        Log = log;
        SettingsStore = settingsStore;
        Settings = settings;
        DeviceIdentity = identity;
        Http = http;
        SessionStore = sessionStore;
        Auth = auth;
        Api = api;
        QrRenderer = qrRenderer;
        _mpvOptions = mpvOptions;
        Backend = backend;
        Coordinator = coordinator;
        History = history;
        Favorites = favorites;
        Playlists = playlists;
        QueueStore = queueStore;
        AudioCache = audioCache;
    }

    public AppPaths Paths { get; private set; }
    public RedactedLog Log { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; set; }
    public DeviceIdentity DeviceIdentity { get; }
    public HttpClient Http { get; }
    public DpapiSessionStore SessionStore { get; }
    public AuthService Auth { get; }
    public BodianApiClient Api { get; }
    public ZxingQrCodeRenderer QrRenderer { get; }
    public MpvPlaybackBackend Backend { get; }
    public PlaybackCoordinator Coordinator { get; }
    public HistoryService History { get; }
    public FavoriteService Favorites { get; }
    public UserPlaylistService Playlists { get; }
    public QueueStateStore QueueStore { get; }
    public IAudioCacheService AudioCache { get; }


    public string? MpvPath => string.IsNullOrWhiteSpace(_mpvOptions.ExecutablePath) ? null : _mpvOptions.ExecutablePath;

    public string? MpvUserPath => string.IsNullOrWhiteSpace(Settings.MpvPath) ? null : Settings.MpvPath;

    public MpvSourceKind? MpvSource { get; private set; }

    public IReadOnlyList<MpvCandidate> MpvCandidates { get; private set; } = [];

    public MpvProbeResult? MpvProbe { get; private set; }

    public string LocalBackendPath => Path.Combine(Paths.BackendDirectory, MpvLocator.ExecutableName);

    public bool LocalBackendExists => File.Exists(LocalBackendPath);

    public static AppServices Create()
    {
        var paths = DataRootMigration.ApplyPending(new AppPaths());
        paths.EnsureCreated();
        var log = new RedactedLog(paths.LogPath);
        var settingsStore = new SettingsStore(paths.SettingsPath);
        var settings = settingsStore.Load();
        var identity = new DeviceIdentity(paths.DeviceIdPath);
        identity.EnsurePersisted();

        var handler = new PublicHeaderHandler(identity) { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sessionStore = new DpapiSessionStore(paths.SessionPath);
        var auth = new AuthService(http, sessionStore, identity, log: log.Write);
        var api = new BodianApiClient(http, auth, log.Write);
        var qrRenderer = new ZxingQrCodeRenderer();

        var mpvOptions = new MpvOptions { Volume = settings.Volume, Log = log.Write };
        var backend = new MpvPlaybackBackend(mpvOptions);
        var audioCache = new AudioCacheService(paths.AudioCachePath, http, log: log.Write);
        var coordinator = new PlaybackCoordinator(
            api,
            backend,
            cacheService: audioCache,
            volume: settings.Volume,
            enableAudioCache: settings.EnableAudioCache,
            audioCacheLimitMb: settings.AudioCacheLimitMb)
        {
            PlayMode = Enum.TryParse<PlaybackMode>(settings.PlayMode, out var parsedMode) ? parsedMode : PlaybackMode.RepeatAll,
            SkipUnplayable = settings.AutoSkipUnplayable,
            RetryOnFailure = settings.RetryOnceOnFailure,
            PreferredQuality = settings.PreferredQuality
        };

        var history = new HistoryService(paths.HistoryPath, log: log.Write);
        coordinator.SongStarted += (_, song) => _ = history.AddRecentSongAsync(song);

        var favorites = new FavoriteService(paths.FavoritesPath, api, auth, log: log.Write);
        var playlists = new UserPlaylistService(paths.PlaylistsPath, api, auth, log: log.Write);
        var queueStore = new QueueStateStore(paths.QueuePath);

        var services = new AppServices(paths, log, settingsStore, settings, identity, http, sessionStore, auth, api, qrRenderer, mpvOptions, backend, coordinator, history, favorites, playlists, queueStore, audioCache);
        services.DetectBackend();
        return services;
    }

    private void DetectBackend()
    {
        MpvCandidates = MpvLocator.FindAll(Settings.MpvPath, localAppDataRoot: Paths.Root);
        var chosen = MpvCandidates.FirstOrDefault();
        MpvSource = chosen?.Source;
        _mpvOptions.ExecutablePath = chosen?.Path ?? string.Empty;
        Log.Write($"backend detect candidates={MpvCandidates.Count} source={MpvSource?.ToString() ?? "none"}");
    }

    public async Task<bool> RefreshBackendAsync(CancellationToken cancellationToken)
    {
        var previous = _mpvOptions.ExecutablePath;
        MpvCandidates = MpvLocator.FindAll(Settings.MpvPath, localAppDataRoot: Paths.Root);
        var chosen = MpvCandidates.FirstOrDefault();
        var next = chosen?.Path ?? string.Empty;
        var changed = !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase);
        MpvSource = chosen?.Source;
        await Backend.SetExecutablePathAsync(next, cancellationToken).ConfigureAwait(false);
        Log.Write($"backend detect candidates={MpvCandidates.Count} source={MpvSource?.ToString() ?? "none"} changed={changed}");
        await ProbeBackendAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<MpvProbeResult> ProbeBackendAsync(CancellationToken cancellationToken)
    {
        MpvProbe = await MpvLocator.ProbeAsync(MpvPath, cancellationToken).ConfigureAwait(false);
        Log.Write($"backend probe ok={MpvProbe.IsMpv} version={MpvProbe.Version ?? "-"}");
        return MpvProbe;
    }

    public async Task<MpvProbeResult> AdoptBackendPathAsync(string path, CancellationToken cancellationToken)
    {
        await Backend.SetExecutablePathAsync(path, cancellationToken).ConfigureAwait(false);
        Settings.MpvPath = path;
        SaveSettings();
        MpvSource = MpvSourceKind.UserSetting;
        MpvCandidates = MpvLocator.FindAll(Settings.MpvPath, localAppDataRoot: Paths.Root);
        return await ProbeBackendAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MpvProbeResult> ClearUserBackendPathAsync(CancellationToken cancellationToken)
    {
        Settings.MpvPath = null;
        SaveSettings();
        await RefreshBackendAsync(cancellationToken).ConfigureAwait(false);
        return MpvProbe ?? MpvProbeResult.NotFound;
    }

    public async Task<(bool Ok, string Message)> CopyBackendToLocalAsync(CancellationToken cancellationToken)
    {
        var source = MpvPath;
        if (source is null || !File.Exists(source)) return (false, "当前没有可用的后端文件，请先重新检测或指定 mpv。");
        var sourceDirectory = Path.GetDirectoryName(source);
        if (string.IsNullOrEmpty(sourceDirectory)) return (false, "无法确定后端文件所在目录。");

        try
        {
            Directory.CreateDirectory(Paths.BackendDirectory);
            long totalBytes = 0;
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(sourceDirectory))
            {
                var name = Path.GetFileName(file);

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(Paths.BackendDirectory, name);
                await using (var input = File.OpenRead(file))
                await using (var output = File.Create(target))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
                totalBytes += new FileInfo(target).Length;
                count++;
            }

            var localExecutable = LocalBackendPath;
            if (!File.Exists(localExecutable)) return (false, "复制完成但没有找到 mpv.exe，请手动指定。");
            var probe = await AdoptBackendPathAsync(localExecutable, cancellationToken).ConfigureAwait(false);
            Log.Write($"backend copy files={count} bytes={totalBytes} mpv={probe.IsMpv}");
            return (true, $"已复制 {count} 个文件（{totalBytes / 1024 / 1024} MB）到本机后端目录，并切换为使用它。{probe.Summary}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (false, $"复制失败：{exception.Message}");
        }
    }

    public string BackendSummary()
    {
        if (!Backend.IsConfigured) return "未找到 mpv 后端";
        var version = MpvProbe is { IsMpv: true } probe ? $"mpv {probe.Version}" : "mpv";
        return Backend.IsRunning ? $"后端运行中 · {version} · PID {Backend.ProcessId}" : $"后端未启动 · {version} 已就绪";
    }

    public void SaveSettings()
    {
        if (Coordinator is not null)
        {
            Settings.Volume = Coordinator.Volume;
        }
        SettingsStore.Save(Settings);
    }

    /// <summary>切换数据根目录</summary>
    public void SwitchDataRoot(string newRoot)
    {
        SaveSettings();
        DataRootMigration.Schedule(Paths, newRoot);
    }

    public void Dispose()
    {
        SaveSettings();
        Http.Dispose();
        Log.Dispose();
    }
}
