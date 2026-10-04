using Dianbo.Core.Services;
using Dianbo.Infrastructure.Api;
using Dianbo.Infrastructure.Auth;
using Dianbo.Infrastructure.Storage;

namespace Dianbo.Tests;

/// <summary>
/// 有界的线上连通性检查（只读、少量请求、不落盘）：
/// 搜索、歌词、播放地址与二维码创建。用于确认协议封装仍然可用。
/// 用法：Dianbo.Tests.exe live
/// </summary>
public static class LiveChecks
{
    public static async Task<int> RunAsync(string[] args)
    {
        // 检查用临时目录，不改动正式数据目录。
        var root = Path.Combine(Path.GetTempPath(), "DianboLiveCheck", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var log = new RedactedLog(paths.LogPath);
        var identity = new DeviceIdentity(paths.DeviceIdPath);
        var handler = new PublicHeaderHandler(identity) { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var credentials = new FixedCredentials(identity.DevId);
        var api = new BodianApiClient(http, credentials, message =>
        {
            log.Write(message);
            Console.WriteLine($"  [api] {message}");
        });

        var keyword = args.Length > 1 ? args[1] : "beyond";
        var failures = 0;

        try
        {
            var songs = await api.SearchAsync(keyword, 0, 5, CancellationToken.None);
            Console.WriteLine($"搜索 “{keyword}”：返回 {songs.Count} 首");            foreach (var song in songs)
            {
                Console.WriteLine($"  id={song.Id} name={song.Name} artist={song.Artist} duration={song.Duration?.TotalSeconds ?? 0:0}s cover={(string.IsNullOrEmpty(song.CoverUrl) ? "无" : "有")}");
            }
            if (songs.Count == 0) failures++;
            else
            {
                var first = songs[0];
                var lyrics = await api.GetLyricsAsync(first.Id, CancellationToken.None);
                Console.WriteLine($"歌词：{(lyrics is null ? "无" : $"{lyrics.Length} 字符")}");
                var document = Dianbo.Core.Lyrics.LyricParser.Parse(lyrics);
                Console.WriteLine($"  解析：{document.Groups.Count} 组，时间轴={document.HasTimeline}，标题={(document.Title ?? "无")}，未配对={document.UnpairedLines.Count}");
                if (document.HasTimeline)
                {
                    var sample = document.Groups[Math.Min(5, document.Groups.Count - 1)];
                    Console.WriteLine($"  第 6 组起始 {sample.StartTimeMs} ms，原文行数 {sample.OriginalLines.Count}，翻译行数 {sample.TranslationLines.Count}");
                }

                // 未登录时权限接口应当失败或返回不可播放；两种情况都说明请求位置正确。
                try
                {
                    var source = await api.ResolveAudioSourceAsync(first.Id, "128kmp3", CancellationToken.None);
                    Console.WriteLine($"播放地址（未登录）：已取得，scheme={new Uri(source.Url).Scheme}，format={source.Format}，bitrate={source.Bitrate}");
                }
                catch (BodianApiException exception)
                {
                    Console.WriteLine($"播放地址（未登录）：被拒绝 code={exception.BusinessCode?.ToString() ?? "-"} http={exception.HttpStatus?.ToString() ?? "-"} msg={exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"搜索阶段失败：{exception.GetType().Name} {exception.Message}");
        }

        // 二维码创建：只创建，不轮询、不兑换、不保存。
        try
        {
            var auth = new Dianbo.Infrastructure.Auth.AuthService(
                http,
                new TransientSessionStore(),
                identity,
                log: log.Write);
            var ticket = await auth.CreateQrCodeAsync(CancellationToken.None);
            var scanUrl = ticket.ScanUrl;
            var prefix = "https://bodian-oia.kuwo.cn/bodian/download.html?pageName=login_pc&pt=3&id=";
            Console.WriteLine($"二维码：创建成功，模板匹配={scanUrl.StartsWith(prefix, StringComparison.Ordinal)}，id 长度={ticket.QrCode.Length}");
            if (!scanUrl.StartsWith(prefix, StringComparison.Ordinal)) failures++;

            // 只观察等待态：不扫码、不兑换。用来确认轮询接口的状态字段形态没有变
            // （未扫码必须一直是"等待"，一旦变成不可用，界面会在用户扫码前就放弃）。
            for (var index = 1; index <= 3; index++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                var state = await auth.PollQrCodeAsync(ticket.QrCode, CancellationToken.None);
                Console.WriteLine($"  轮询 {index}：{state}");
                if (state != QrPollState.Waiting)
                {
                    Console.WriteLine("  警告：未扫码却没有处于等待态");
                    failures++;
                }
            }
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"二维码创建失败：{exception.GetType().Name} {exception.Message}");
        }

        Console.WriteLine(failures == 0 ? "线上检查：全部通过" : $"线上检查：{failures} 项失败");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>单实例检查：连续启动两次，只应留下一个进程和一个窗口。</summary>
    public static async Task<int> RunSingleInstanceAsync(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Console.WriteLine("用法：Dianbo.Tests.exe single <Dianbo.exe 路径>");
            return 2;
        }

        var exe = args[1];
        var failures = 0;
        foreach (var running in System.Diagnostics.Process.GetProcessesByName("Dianbo"))
        {
            Console.WriteLine($"提示：先关闭正在运行的实例 pid={running.Id}");
            failures++;
        }
        if (failures > 0) return 1;

        System.Diagnostics.Process? first = null;
        System.Diagnostics.Process? second = null;
        try
        {
            first = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
            Console.WriteLine($"第一次启动：pid={first?.Id}");
            if (first is null)
            {
                Console.WriteLine("第一次启动失败：无法创建进程");
                return 1;
            }
            // 等第一个实例建好窗口并取到互斥量。
            await Task.Delay(6000);
            if (first.HasExited)
            {
                Console.WriteLine($"第一次实例提前退出，退出码 {first.ExitCode}");
                return 1;
            }

            second = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
            Console.WriteLine($"第二次启动：pid={second?.Id}");
            await Task.Delay(6000);

            var secondExited = second?.HasExited ?? true;
            var alive = System.Diagnostics.Process.GetProcessesByName("Dianbo");
            var withWindow = alive.Count(process => process.MainWindowHandle != IntPtr.Zero);
            Console.WriteLine($"第二次实例已退出={secondExited}，当前进程数={alive.Length}，其中带主窗口={withWindow}");
            foreach (var process in alive) Console.WriteLine($"  pid={process.Id} title={process.MainWindowTitle}");

            if (!secondExited) failures++;
            if (alive.Length != 1) failures++;
            if (withWindow != 1) failures++;
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"单实例检查失败：{exception.GetType().Name} {exception.Message}");
        }
        finally
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("Dianbo"))
            {
                try { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
                catch (Exception) { }
            }
        }

        Console.WriteLine(failures == 0 ? "单实例检查：通过" : $"单实例检查：{failures} 项失败");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>后端进程与 IPC 生命周期：启动、命令、音量、停止、无孤儿进程。</summary>
    public static async Task<int> RunBackendAsync(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Console.WriteLine("用法：Dianbo.Tests.exe backend <mpv.exe 路径>");
            return 2;
        }

        var failures = 0;
        var backend = new Dianbo.Infrastructure.Playback.MpvPlaybackBackend(new Dianbo.Infrastructure.Playback.MpvOptions
        {
            ExecutablePath = args[1],
            Volume = 20,
            ProgressInterval = TimeSpan.FromMilliseconds(400),
            Log = Console.WriteLine
        });

        try
        {
            await backend.StartAsync(CancellationToken.None);
            Console.WriteLine($"后端启动：running={backend.IsRunning} pid={backend.ProcessId}");
            if (!backend.IsRunning) failures++;
            Console.WriteLine($"Job Object 兜底（异常退出由系统结束）：guard={backend.HasJobObjectGuard}");
            if (!backend.HasJobObjectGuard) failures++;

            await backend.SetVolumeAsync(35, CancellationToken.None);
            Console.WriteLine("音量命令：已接受");

            // 使用本地不存在的文件路径验证错误路径不会被当作成功 EOF 处理。
            var started = DateTime.UtcNow;
            using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var seen = new List<string>();
            try
            {
                await backend.LoadAsync("https://127.0.0.1:1/not-exists.mp3", 1, 999, CancellationToken.None);
                await foreach (var snapshot in backend.ObserveAsync(1, 999, observeCts.Token))
                {
                    seen.Add($"{snapshot.State}/{snapshot.EndReason}/pos={snapshot.Position.TotalSeconds:0.0}");
                    if (snapshot.EndReason != Dianbo.Core.Models.PlaybackEndReason.None) break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            Console.WriteLine($"观察 {seen.Count} 个快照，用时 {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
            foreach (var item in seen.Take(3)) Console.WriteLine($"  {item}");
            Console.WriteLine($"  最后：{seen.LastOrDefault() ?? "无"}");
            if (seen.Count == 0) failures++;
            // 失败媒体不能报告为成功 EOF。
            if (seen.Any(item => item.Contains("EndOfFile"))) failures++;

            // 成功路径：用本地生成的 WAV 验证"真的播起来 → 位置推进 → 自然 EOF 报 EndOfFile"。
            // 不联网、不需要账号，因此这一步能补上"只有失败样本"的空白。
            var wavDirectory = Path.Combine(Environment.CurrentDirectory, ".scratch", "backend-check");
            Directory.CreateDirectory(wavDirectory);
            var wav = Path.Combine(wavDirectory, $"tone-{Guid.NewGuid():N}.wav");
            WriteToneWav(wav, seconds: 3);
            try
            {
                await backend.LoadAsync(wav, 2, 998, CancellationToken.None);
                await backend.SetPausedAsync(false, CancellationToken.None);

                var sawPlaying = false;
                var maxPosition = 0d;
                var endReason = Dianbo.Core.Models.PlaybackEndReason.None;
                using var playCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                try
                {
                    await foreach (var snapshot in backend.ObserveAsync(2, 998, playCts.Token))
                    {
                        sawPlaying |= snapshot.State == Dianbo.Core.Models.PlaybackState.Playing;
                        maxPosition = Math.Max(maxPosition, snapshot.Position.TotalSeconds);
                        if (snapshot.EndReason != Dianbo.Core.Models.PlaybackEndReason.None)
                        {
                            endReason = snapshot.EndReason;
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }

                Console.WriteLine($"成功播放路径：playing={sawPlaying} 最大位置={maxPosition:0.0}s 结束原因={endReason}");
                if (!sawPlaying) failures++;
                if (maxPosition < 2.0) failures++;
                if (endReason != Dianbo.Core.Models.PlaybackEndReason.EndOfFile)
                {
                    Console.WriteLine("  警告：自然结束没有报告为 EOF");
                    failures++;
                }
            }
            finally
            {
                try { File.Delete(wav); } catch (Exception) { }
            }

            // Job Object 兜底验证：把子进程放进 KILL_ON_JOB_CLOSE 的 Job，
            // 只关闭句柄（相当于应用被强杀），系统就应该结束该进程。
            Console.WriteLine("Job Object 兜底验证：");
            var job = Dianbo.Infrastructure.Playback.ChildProcessJob.TryCreate(Console.WriteLine);
            if (job is null)
            {
                Console.WriteLine("  无法创建 Job Object，跳过该项");
                failures++;
            }
            else
            {
                using (job)
                {
                    var startInfo = new System.Diagnostics.ProcessStartInfo(args[1])
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(args[1]) ?? Environment.CurrentDirectory
                    };
                    foreach (var argument in new[] { "--no-config", "--idle=yes", "--terminal=no", "--vid=no" })
                        startInfo.ArgumentList.Add(argument);

                    var child = System.Diagnostics.Process.Start(startInfo);
                    if (child is null)
                    {
                        Console.WriteLine("  无法启动子进程，跳过该项");
                        failures++;
                    }
                    else
                    {
                        using (child)
                        {
                            var assigned = job.TryAssign(child);
                            Console.WriteLine($"  加入 Job：pid={child.Id} assigned={assigned}");
                            if (!assigned) failures++;

                            await Task.Delay(1000);
                            if (child.HasExited)
                            {
                                Console.WriteLine("  子进程提前退出，无法验证兜底清理");
                                failures++;
                            }
                            else
                            {
                                // 只关闭句柄，不主动结束进程：这一步必须由系统完成。
                                job.Dispose();
                                var killed = child.WaitForExit(5000);
                                Console.WriteLine($"  关闭 Job 句柄后子进程已结束={killed}");
                                if (!killed) failures++;
                                if (!child.HasExited)
                                {
                                    try { child.Kill(entireProcessTree: true); } catch (Exception) { }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"后端检查失败：{exception.GetType().Name} {exception.Message}");
        }
        finally
        {
            var pid = backend.ProcessId;
            await backend.StopAsync(CancellationToken.None);
            await backend.DisposeAsync();
            Console.WriteLine($"后端已停止：running={backend.IsRunning}");
            if (backend.IsRunning) failures++;
            await Task.Delay(500);
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(pid);
                Console.WriteLine($"警告：进程 {pid} 仍存在（{process.ProcessName}）");
                failures++;
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"进程 {pid} 已结束，没有留下孤儿进程");
            }
        }

        Console.WriteLine(failures == 0 ? "后端检查：全部通过" : $"后端检查：{failures} 项失败");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>写一个 16 位单声道 PCM 的正弦波 WAV，用来在本地验证成功播放与自然 EOF。</summary>
    private static void WriteToneWav(string path, int seconds, int sampleRate = 22050)
    {
        var samples = sampleRate * seconds;
        var dataBytes = samples * 2;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);          // PCM
        writer.Write((short)1);          // 单声道
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);    // 字节率
        writer.Write((short)2);          // 块对齐
        writer.Write((short)16);         // 位深
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        for (var index = 0; index < samples; index++)
        {
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * index / sampleRate) * 6000));
        }
    }

    /// <summary>
    /// 用本机保存的会话诊断"权限与播放地址"：只读，不打印票据，也不打印任何地址。
    /// 用来解释"已登录会员却提示没有权限"这类现象。
    /// 用法：Dianbo.Tests.exe rights [关键词]
    /// </summary>
    public static async Task<int> RunRightsAsync(string[] args)
    {
        var keyword = args.Length > 1 ? args[1] : "beyond";
        var paths = new AppPaths();
        paths.EnsureCreated();
        var log = new RedactedLog(paths.LogPath);
        var identity = new DeviceIdentity(paths.DeviceIdPath);
        var handler = new PublicHeaderHandler(identity) { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var store = new Dianbo.Infrastructure.Auth.DpapiSessionStore(paths.SessionPath);
        var session = await store.LoadAsync(cts.Token);
        if (session is null)
        {
            Console.WriteLine("没有可用的本机会话：请先在应用里完成二维码登录。");
            return 2;
        }
        Console.WriteLine($"已加载本机会话（uid 与票据不打印），devid 长度={session.DevId.Length}");
        Console.WriteLine($"会话文件：{paths.SessionPath}");

        var api = new BodianApiClient(http, new SessionCredentials(session), message => Console.WriteLine($"  [api] {message}"));
        var songs = await api.SearchAsync(keyword, 0, 5, cts.Token);
        Console.WriteLine($"搜索“{keyword}”返回 {songs.Count} 首");
        foreach (var song in songs)
        {
            Console.WriteLine($"《{song.Name}》 id={song.Id} artist={song.Artist} 列表时长={song.Duration?.TotalSeconds ?? 0:0}s");
            try
            {
                var allowed = await api.CheckPlayRightAsync(song.Id, cts.Token);
                Console.WriteLine($"  checkRight -> {allowed}");
            }
            catch (BodianApiException exception)
            {
                Console.WriteLine($"  checkRight 失败 code={exception.BusinessCode?.ToString() ?? "-"} {exception.Message}");
            }
            try
            {
                var source = await api.ResolveAudioSourceAsync(song.Id, "128kmp3", cts.Token);
                var seconds = source.Duration?.TotalSeconds ?? 0;
                var trial = source.IsTrialClip(song.Duration) ? "（疑似试听片段）" : string.Empty;
                Console.WriteLine($"  audioUrl -> 有地址 format={source.Format} bitrate={source.Bitrate} size={source.Size} 音频时长={seconds:0}s{trial}");
            }
            catch (BodianApiException exception)
            {
                Console.WriteLine($"  audioUrl 失败 code={exception.BusinessCode?.ToString() ?? "-"} {exception.Message}");
            }
        }
        return 0;
    }

    public static async Task<int> RunLibraryLiveAsync(string[] args)
    {
        var paths = new AppPaths();
        paths.EnsureCreated();
        var sessionStore = new DpapiSessionStore(paths.SessionPath);
        var session = await sessionStore.LoadAsync(CancellationToken.None);
        if (session is null)
        {
            Console.WriteLine("未找到已保存的登录会话，请先在客户端中扫码登录。");
            return 1;
        }

        Console.WriteLine($"找到登录会话：UID={session.Uid}");
        var identity = new DeviceIdentity(paths.DeviceIdPath);
        var handler = new PublicHeaderHandler(identity) { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var credentials = new SessionCredentials(session);
        var api = new BodianApiClient(http, credentials, s => Console.WriteLine($"  [api] {s}"));

        Console.WriteLine("\n--- 1. 获取「我喜欢」歌单 ---");
        var fond = await api.GetFondPlaylistAsync(CancellationToken.None);
        if (fond is null)
        {
            Console.WriteLine("获取我喜欢歌单失败或为空");
        }
        else
        {
            Console.WriteLine($"歌单名称: {fond.Name}, ID: {fond.Id}, 歌曲数: {fond.MusicCount}, 封面: {fond.CoverUrl}");
            Console.WriteLine($"--- 获取「我喜欢」中的前 5 首歌 (source=5) ---");
            var fondSongs = await api.GetPlaylistSongsAsync(fond.Id, 0, 5, CancellationToken.None);
            Console.WriteLine($"实际获取到 {fondSongs.Count} 首歌：");
            foreach (var song in fondSongs)
            {
                Console.WriteLine($"  [{song.Id}] {song.Name} - {song.Artist} ({song.Duration?.ToString(@"mm\:ss") ?? "-"})");
            }
        }

        Console.WriteLine("\n--- 2. 获取自建歌单列表 ---");
        var userPlaylists = await api.GetUserPlaylistsAsync(0, 10, CancellationToken.None);
        Console.WriteLine($"获取到 {userPlaylists.Count} 个自建歌单：");
        foreach (var pl in userPlaylists)
        {
            Console.WriteLine($"  歌单 [{pl.Id}] {pl.Name} ({pl.MusicCount} 首) 创建时间: {pl.CreateTime}");
        }

        if (userPlaylists.Count > 0)
        {
            var target = userPlaylists[0];
            Console.WriteLine($"--- 获取自建歌单 [{target.Name}] 前 100 首歌 ---");
            var upSongs = await api.GetPlaylistSongsAsync(target.Id, 0, 100, CancellationToken.None);
            Console.WriteLine($"成功获取到 {upSongs.Count} 首歌！");
            foreach (var song in upSongs.Take(3))
            {
                Console.WriteLine($"  [{song.Id}] {song.Name} - {song.Artist}");
            }
        }

        Console.WriteLine("\n--- 3. 获取本地最近播放记录 ---");
        var history = new HistoryService(paths.HistoryPath);
        var recents = await history.GetRecentSongsAsync(10);
        Console.WriteLine($"本地历史记录共 {recents.Count} 首：");
        foreach (var song in recents.Take(5))
        {
            Console.WriteLine($"  [{song.Id}] {song.Name} - {song.Artist} ({song.Duration?.ToString(@"mm\:ss") ?? "-"})");
        }

        return 0;
    }

    /// <summary>只读地使用本机会话，不修改任何内容。</summary>
    private sealed class SessionCredentials : IAuthCredentialSource
    {
        public SessionCredentials(Dianbo.Core.Models.Session session) => Session = session;
        public Dianbo.Core.Models.Session? Session { get; }
        public string DevId => Session?.DevId ?? string.Empty;
    }

    private sealed class FixedCredentials : IAuthCredentialSource
    {
        public FixedCredentials(string devId) => DevId = devId;
        public Dianbo.Core.Models.Session? Session => null;
        public string DevId { get; }
    }

    /// <summary>在线检查不写入任何会话文件。</summary>
    private sealed class TransientSessionStore : ISessionStore
    {
        public string Location => "（未使用）";
        public Task<Dianbo.Core.Models.Session?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<Dianbo.Core.Models.Session?>(null);
        public Task SaveAsync(Dianbo.Core.Models.Session session, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
