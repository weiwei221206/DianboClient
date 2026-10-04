using System.Text;
using System.Text.Json;
using Dianbo.Core.Lyrics;
using Dianbo.Core.Models;
using Dianbo.Core.Services;
using Dianbo.Infrastructure.Api;
using Dianbo.Infrastructure.Auth;
using Dianbo.Infrastructure.Playback;
using Dianbo.Infrastructure.Storage;

namespace Dianbo.Tests;

/// <summary>
/// 轻量自检程序：不依赖测试框架，覆盖确定性逻辑与 IPC 解析约束。
/// 退出码 0 表示全部通过。
/// </summary>
public static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = [];

    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "live", StringComparison.OrdinalIgnoreCase))
            return LiveChecks.RunAsync(args).GetAwaiter().GetResult();
        if (args.Length > 0 && string.Equals(args[0], "backend", StringComparison.OrdinalIgnoreCase))
            return LiveChecks.RunBackendAsync(args).GetAwaiter().GetResult();
        if (args.Length > 0 && string.Equals(args[0], "single", StringComparison.OrdinalIgnoreCase))
            return LiveChecks.RunSingleInstanceAsync(args).GetAwaiter().GetResult();
        if (args.Length > 0 && string.Equals(args[0], "rights", StringComparison.OrdinalIgnoreCase))
            return LiveChecks.RunRightsAsync(args).GetAwaiter().GetResult();
        if (args.Length > 0 && string.Equals(args[0], "library", StringComparison.OrdinalIgnoreCase))
            return LiveChecks.RunLibraryLiveAsync(args).GetAwaiter().GetResult();

        foreach (var result in ReviewRegressionChecks.RunAsync().GetAwaiter().GetResult())
            Check(result.Name, result.Error is null, result.Error);
        LyricTests();
        TimelineTests();
        IpcProtocolTests();
        BackendTests();
        QrRenderTests();
        StorageTests();
        UserProfileTests();
        PlaylistAndHistoryTests();
        QualityAndAudioSourceTests();
        FavoriteServiceTests();
        UserPlaylistServiceTests();
        QueueAndPlayModeTests();
        RecommendationRadioTests();
        AudioCacheTests();

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed} 项，失败 {Failures.Count} 项。");
        foreach (var failure in Failures) Console.WriteLine($"  失败：{failure}");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            return;
        }
        Failures.Add(detail is null ? name : $"{name} — {detail}");
    }

    private static void Equal<T>(string name, T expected, T actual)
        => Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"期望 {expected}，实际 {actual}");

    private static void LyricTests()
    {
        // 空文档
        Check("空歌词返回空文档", !LyricParser.Parse(null).HasTimeline);
        Check("空字符串返回空文档", LyricParser.Parse("   ").Groups.Count == 0);

        // 单行与时间段位
        var single = LyricParser.Parse("[00:01.500]第一行");
        Equal("单行组数", 1, single.Groups.Count);
        Equal("单行时间毫秒", 1500L, single.Groups[0].StartTimeMs);
        Equal("单行文本", "第一行", single.Groups[0].OriginalText);

        // 一位与三位小数位
        var fractions = LyricParser.Parse("[00:01.5]a\n[00:02.05]b\n[00:03.005]c");
        Equal("一位小数", 1500L, fractions.Groups[0].StartTimeMs);
        Equal("两位小数", 2050L, fractions.Groups[1].StartTimeMs);
        Equal("三位小数", 3005L, fractions.Groups[2].StartTimeMs);

        // 同一行多个时间戳必须全部展开
        var multi = LyricParser.Parse("[00:02.000][00:04.000]重复行");
        Equal("多时间戳展开为两组", 2, multi.Groups.Count);
        Equal("多时间戳组1文本", "重复行", multi.Groups[0].OriginalText);
        Equal("多时间戳组2时间", 4000L, multi.Groups[1].StartTimeMs);

        // 同一时间点原文全部保留
        var duplicate = LyricParser.Parse("[00:05.000]第一句\n[00:05.000]第二句");
        Equal("同时间点合并为一组", 1, duplicate.Groups.Count);
        Equal("同时间点原文行数", 2, duplicate.Groups[0].OriginalLines.Count);

        // 完全相同的重复时间戳行不重复显示
        var repeated = LyricParser.Parse("[00:05.000]完全一样\n[00:05.000]完全一样");
        Equal("重复时间戳不重复显示", 1, repeated.Groups[0].OriginalLines.Count);

        // 附加的原文行不能因为长度接近就被当成翻译
        var extraOriginal = LyricParser.Parse("[00:06.000]短句甲\n[00:06.000]短句乙");
        Equal("附加原文行保持为原文", 2, extraOriginal.Groups[0].OriginalLines.Count);
        Equal("附加原文行不产生翻译", 0, extraOriginal.Groups[0].TranslationLines.Count);

        // 行内逐字标签被去掉，且不解释时长
        var inline = LyricParser.Parse("[00:10.000]<1533,1523>带<2921,151>标签的<3765,3755>行");
        Equal("行内标签已去除", "带标签的行", inline.Groups[0].OriginalText);

        // 同时间戳的翻译无法与“同时间点另一句原文”区分，因此保留为原文行（保守选择）
        var sameStamp = LyricParser.Parse("[00:01.000]原文一\n[00:01.000]translation one\n[00:02.000]原文二\n[00:02.000]translation two");
        Equal("同时间戳按组保留", 2, sameStamp.Groups.Count);
        Equal("同时间戳不猜测翻译", 0, sameStamp.Groups[0].TranslationLines.Count);
        Equal("同时间戳原文全部保留", 2, sameStamp.Groups[0].OriginalLines.Count);

        // 旧项目的 <0,0> 是明确的译文标记，即使它带有同一时间戳也不作为原文。
        var marked = LyricParser.Parse("[00:01.000]<120,80>原文一\n[00:01.000]<0,0>translation one\n[00:02.000]原文二\n[00:02.000]<0,0>translation two");
        Equal("标记译文仍只有两组", 2, marked.Groups.Count);
        Equal("标记译文配到第一组", "translation one", marked.Groups[0].TranslationText);
        Equal("标记译文配到第二组", "translation two", marked.Groups[1].TranslationText);
        Equal("标记译文不混入原文", "原文一", marked.Groups[0].OriginalText);

        var orphanMarked = LyricParser.Parse("[00:00.000]<0,0>孤立译文\n[00:01.000]原文");
        Equal("孤立标记译文不创建时间组", 1, orphanMarked.Groups.Count);
        Check("孤立标记译文得到保留", orphanMarked.UnpairedLines.Contains("孤立译文"));

        // 无时间戳的翻译行：按“出现在原文之后、下一行之前”的顺序关系配对到前一组
        var translated = LyricParser.Parse("[00:01.000]原文一\ntranslation one\n[00:02.000]原文二\ntranslation two");
        Equal("独立翻译配对组数", 2, translated.Groups.Count);
        Equal("独立翻译配对到上一组", "translation one", translated.Groups[0].TranslationText);
        Equal("末尾独立翻译配对到末组", "translation two", translated.Groups[1].TranslationText);

        // 文档开头的孤立行没有可配对的上一组，保留为未配对内容
        var orphan = LyricParser.Parse("孤立的说明行\n[00:01.000]原文");
        Equal("开头孤立行不挂到歌词组", 0, orphan.Groups[0].TranslationLines.Count);
        Check("开头孤立行保留为未配对内容", orphan.UnpairedLines.Contains("孤立的说明行"), string.Join("|", orphan.UnpairedLines));
        Equal("开头孤立行不影响时间轴", 1, orphan.Groups.Count);

        // 格式内 offset 保留原值，不与用户偏移混用
        var offset = LyricParser.Parse("[offset:-500]\n[00:01.000]原文");
        Equal("offset 解析为 -500", -500L, offset.SourceOffsetMs);
        Equal("offset 不影响组时间", 1000L, offset.Groups[0].StartTimeMs);

        // 制作信息行与普通歌词一样保留在时间轴中
        var metadata = LyricParser.Parse("[ti:测试歌曲]\n[00:01.000]作词：某人\n[00:02.000]真正的歌词");
        Equal("ti 解析为标题", "测试歌曲", metadata.Title ?? string.Empty);
        Equal("制作信息行保留", "作词：某人", metadata.Groups[0].OriginalText);
        Equal("演唱行保留", "真正的歌词", metadata.Groups[1].OriginalText);

        // 无时间戳说明行按顺序关系保留，不导致整行丢失
        var junk = LyricParser.Parse("[00:01.000]正常行\n这是一行没有时间戳的说明");
        Equal("无时间戳说明行配对到末组", 1, junk.Groups[0].TranslationLines.Count);
        Equal("正常行仍在时间轴", 1, junk.Groups.Count);
    }

    private static void TimelineTests()
    {
        var document = LyricParser.Parse("[00:01.000]A\n[00:02.000]B\n[00:03.000]C");

        Equal("首时间点之前定位为 -1", -1, document.IndexAt(TimeSpan.FromSeconds(0.9)));
        Equal("精确边界取本行", 0, document.IndexAt(TimeSpan.FromSeconds(1.0)));
        Equal("区间中间取前一行", 0, document.IndexAt(TimeSpan.FromSeconds(1.5)));
        Equal("第二行边界", 1, document.IndexAt(TimeSpan.FromSeconds(2.0)));
        Equal("末行之后取末行", 2, document.IndexAt(TimeSpan.FromSeconds(99)));
        Equal("负时间同样在首行之前", -1, document.IndexAt(TimeSpan.FromSeconds(-5)));

        // 同时间点重复不破坏二分查找
        var duplicate = LyricParser.Parse("[00:01.000]A\n[00:01.000]B\n[00:02.000]C");
        Equal("重复时间点组数", 2, duplicate.Groups.Count);
        Equal("重复时间点定位", 0, duplicate.IndexAt(TimeSpan.FromSeconds(1.2)));
        Equal("重复时间点后定位", 1, duplicate.IndexAt(TimeSpan.FromSeconds(2.5)));
        var empty = LyricDocument.Empty;
        Equal("空文档定位为 -1", -1, empty.IndexAt(TimeSpan.FromSeconds(10)));
    }

    private static void IpcProtocolTests()
    {
        // 后端枚举与接口契约
        Check("EOF 与 stop 是不同原因",
            Enum.IsDefined(typeof(Dianbo.Core.Models.PlaybackEndReason), Dianbo.Core.Models.PlaybackEndReason.EndOfFile)
            && Enum.IsDefined(typeof(Dianbo.Core.Models.PlaybackEndReason), Dianbo.Core.Models.PlaybackEndReason.Stopped));

        // 媒体地址不入日志：兜底脱敏必须移除查询串
        var sanitized = Dianbo.Infrastructure.Storage.RedactedLog.Sanitize("load https://cdn.example.com/a.mp3?token=secret end");
        Check("脱敏移除媒体地址", !sanitized.Contains("secret"), sanitized);
        Check("脱敏保留其余文本", sanitized.Contains("end"), sanitized);
        Check("脱敏处理 http", Dianbo.Infrastructure.Storage.RedactedLog.Sanitize("x http://a/b?c=1 y") == "x [地址已省略] y");

        // IPC 命令请求编号递增且回复可关联：用真实连接对象验证编号分配
        Check("MPV 后端可构造", new MpvPlaybackBackend(new MpvOptions { ExecutablePath = "mpv.exe" }).IsRunning == false);

        // 会话存储 schema 校验
        try
        {
            Dianbo.Infrastructure.Auth.DpapiSessionStore.Validate(new Dianbo.Core.Models.Session
            {
                Uid = 1,
                Token = "token",
                DevId = "0123456789abcdef0123456789abcdef"
            });
            Check("合法会话通过校验", true);
        }
        catch (Exception exception)
        {
            Check("合法会话通过校验", false, exception.Message);
        }

        foreach (var (label, session) in new (string, Dianbo.Core.Models.Session)[]
        {
            ("uid 非法", new Dianbo.Core.Models.Session { Uid = 0, Token = "t", DevId = "0123456789abcdef0123456789abcdef" }),
            ("token 为空", new Dianbo.Core.Models.Session { Uid = 1, Token = "", DevId = "0123456789abcdef0123456789abcdef" }),
            ("devid 非法", new Dianbo.Core.Models.Session { Uid = 1, Token = "t", DevId = "NOT-HEX" })
        })
        {
            var rejected = false;
            try
            {
                Dianbo.Infrastructure.Auth.DpapiSessionStore.Validate(session);
            }
            catch (Dianbo.Core.Services.BodianApiException)
            {
                rejected = true;
            }
            Check($"会话校验拒绝{label}", rejected);
        }

        // 二维码内容模板与实测一致
        const string expected = "https://bodian-oia.kuwo.cn/bodian/download.html?pageName=login_pc&pt=3&id=";
        Check("二维码模板前缀", expected.EndsWith("id=", StringComparison.Ordinal));

        // 业务错误分类：权限错误不应被当作可重试抖动
        var permission = new Dianbo.Core.Services.BodianApiException("play/music/v2/audioUrl", 200, 11052, "失败", retryable: false);
        Check("权限错误被识别", permission.IsPermissionDenied);
        Check("权限错误不可重试", !permission.Retryable);
        var transient = new Dianbo.Core.Services.BodianApiException("search/music/list", 503, null, "服务不可用");
        Check("网络错误可重试", transient.Retryable);

        // 搜索 DTO 混用类型：size 曾为字符串，转换必须受控
        var json = Encoding.UTF8.GetBytes("""{"code":"200","data":{"size":"12345","bitrate":128,"duration":206}}""");        using var document = System.Text.Json.JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        Equal("字符串 size 转 long", 12345L, Dianbo.Infrastructure.Api.JsonValue.AsLong(data.GetProperty("size")) ?? -1);
        Equal("字符串 code 转 int", 200, Dianbo.Infrastructure.Api.JsonValue.AsInt(document.RootElement.GetProperty("code")) ?? -1);
        Equal("数字 bitrate 转 int", 128, Dianbo.Infrastructure.Api.JsonValue.AsInt(data.GetProperty("bitrate")) ?? -1);
    }

    /// <summary>后端定位与"是否已落地"的确定性检查，全部在临时目录里做，不碰真实数据目录。</summary>
    private static void BackendTests()
    {
        // 版本号解析：只认 mpv 自己的横幅，避免把别的程序当成后端。
        Equal("解析带 v 前缀的版本", "v0.41.0-dev-ga1f50f2c3",
            MpvLocator.ParseVersion("mpv v0.41.0-dev-ga1f50f2c3 Copyright © 2000-2026 mpv/MPlayer/mplayer2 projects"));
        Equal("解析稳定版版本", "0.37.0",
            MpvLocator.ParseVersion("mpv 0.37.0 Copyright © 2000-2024 mpv/MPlayer/mplayer2 projects"));
        Check("无关程序输出不产生版本号", MpvLocator.ParseVersion("Windows PowerShell 5.1.26100.1") is null);
        Check("空输出不产生版本号", MpvLocator.ParseVersion("") is null);
        Check("多行输出里只有 mpv 横幅被识别", MpvLocator.ParseVersion("libplacebo version: v7.372.0\nmpv v0.41.0-dev-x") == "v0.41.0-dev-x");

        // 测试树放在工作区 .scratch 下：本机对子进程在 %TEMP% 里新建目录会被文件权限挡住。
        // 注意：工作区本身是开发检出（有 DianboClient.sln 与 validation 目录），所以从树内任意
        // 目录向上都会命中"开发验证目录"这一个候选。因此下面的断言按**来源**取候选并检查先后，
        // 而不是假定列表里只有自己造的那几个文件。
        var root = Path.Combine(Environment.CurrentDirectory, ".scratch", "locator-test", Guid.NewGuid().ToString("N"));
        try
        {
            var appDirectory = Path.Combine(root, "app");
            var emptyApp = Path.Combine(root, "empty-app");
            var localRoot = Path.Combine(root, "local");
            var extraDirectory = Path.Combine(root, "extra");
            var repository = Path.Combine(root, "repo");
            Directory.CreateDirectory(appDirectory);
            Directory.CreateDirectory(emptyApp);
            Directory.CreateDirectory(localRoot);
            Directory.CreateDirectory(extraDirectory);

            var userMpv = Touch(Path.Combine(root, "user-mpv.exe"));
            var appMpv = Touch(Path.Combine(appDirectory, "mpv", "mpv.exe"));
            var environmentMpv = Touch(Path.Combine(root, "env-mpv.exe"));
            var localMpv = Touch(Path.Combine(localRoot, "mpv", "mpv.exe"));
            var pathMpv = Touch(Path.Combine(extraDirectory, "mpv.exe"));
            var devMpv = Touch(Path.Combine(repository, "validation", ".deps", "mpv-ci", "mpv.exe"));
            File.WriteAllText(Path.Combine(repository, "DianboClient.sln"), string.Empty);
            var nested = Path.Combine(repository, "src", "Dianbo.App", "bin", "Release", "win-x64");
            Directory.CreateDirectory(nested);

            var none = Path.Combine(root, "no-local");
            var missing = Path.Combine(root, "missing.exe");

            // 1) 用户指定优先
            var candidates = MpvLocator.FindAll(userMpv, appDirectory, localRoot, environmentMpv, extraDirectory, appDirectory);
            Equal("用户指定优先", userMpv, candidates[0].Path);
            Equal("来源为用户指定", MpvSourceKind.UserSetting, candidates[0].Source);

            // 2) 用户指定不存在时，环境变量优先于应用目录
            candidates = MpvLocator.FindAll(missing, appDirectory, localRoot, environmentMpv, extraDirectory, appDirectory);
            Equal("环境变量次优先", environmentMpv, candidates[0].Path);
            Equal("来源为环境变量", MpvSourceKind.EnvironmentVariable, candidates[0].Source);

            // 3) 应用目录里的 mpv\mpv.exe
            candidates = MpvLocator.FindAll(null, appDirectory, localRoot, null, extraDirectory, appDirectory);
            Equal("应用目录命中 mpv\\mpv.exe", appMpv, candidates[0].Path);
            Equal("来源为应用目录", MpvSourceKind.AppDirectory, candidates[0].Source);

            // 4) 本机后端目录（%LOCALAPPDATA%\BodianClient\mpv）优先于开发验证目录
            candidates = MpvLocator.FindAll(null, emptyApp, localRoot, null, null, emptyApp);
            Equal("本机后端目录命中", localMpv, candidates[0].Path);
            Equal("来源为本机后端目录", MpvSourceKind.LocalAppData, candidates[0].Source);
            Check("本机后端目录优先于开发验证目录",
                candidates.ToList().FindIndex(c => c.Source == MpvSourceKind.LocalAppData)
                < candidates.ToList().FindIndex(c => c.Source == MpvSourceKind.DevValidation));

            // 5) 开发验证目录：从仓库内任意深度的目录向上查找
            candidates = MpvLocator.FindAll(null, nested, none, null, null, nested);
            Equal("开发验证目录命中", devMpv, candidates[0].Path);
            Equal("来源为开发验证目录", MpvSourceKind.DevValidation, candidates[0].Source);

            // 6) 系统 PATH 排在最后
            candidates = MpvLocator.FindAll(null, emptyApp, none, null, extraDirectory, emptyApp);
            Equal("PATH 命中", pathMpv, candidates.Single(c => c.Source == MpvSourceKind.Path).Path);
            Equal("PATH 是最后一档", MpvSourceKind.Path, candidates[^1].Source);

            // 7) 一档都没有时不编造候选（用仓库之外的既有目录，避免把工作区的开发检出算进来）
            var neutral = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "dianbo-nonexistent");
            Check("全部缺失时没有候选",
                MpvLocator.FindAll(null, neutral, neutral, null, " ", neutral).Count == 0);

            // 8) 同一个文件不因多个来源重复出现
            var deduped = MpvLocator.FindAll(pathMpv, appDirectory, localRoot, pathMpv, extraDirectory, appDirectory);
            Equal("同一个文件只出现一次", 1,
                deduped.Count(candidate => string.Equals(candidate.Path, pathMpv, StringComparison.OrdinalIgnoreCase)));

            // 9) 没有仓库标记时不认开发验证目录（避免误用别的目录里的 mpv）
            var foreign = Path.Combine(root, "foreign", "src", "app");
            var foreignMpv = Touch(Path.Combine(root, "foreign", "validation", ".deps", "mpv-ci", "mpv.exe"));
            Directory.CreateDirectory(foreign);
            var resolved = MpvLocator.FindDevValidation(foreign);
            Check("缺少仓库标记的 validation 目录不被采用",
                resolved is null || !string.Equals(resolved, foreignMpv, StringComparison.OrdinalIgnoreCase), resolved);
            Check("仓库之外不会命中开发验证目录",
                MpvLocator.FindDevValidation(neutral) is null);

            // 10) 后端"是否已落地"的判定
            Check("空路径视为未配置", !new MpvPlaybackBackend(new MpvOptions { ExecutablePath = string.Empty }).IsConfigured);
            Check("不存在的文件视为未配置", !new MpvPlaybackBackend(new MpvOptions { ExecutablePath = missing }).IsConfigured);
            Check("存在的文件视为已配置", new MpvPlaybackBackend(new MpvOptions { ExecutablePath = pathMpv }).IsConfigured);

            // 11) 运行时更换后端路径立即生效，且无需重启应用
            var backend = new MpvPlaybackBackend(new MpvOptions { ExecutablePath = userMpv });
            backend.SetExecutablePathAsync(pathMpv, CancellationToken.None).GetAwaiter().GetResult();
            Equal("运行时更换后端路径", pathMpv, backend.ExecutablePath);
            Check("未启动的后端没有 Job 绑定", !backend.HasJobObjectGuard);
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            // 文件系统层面的问题按一项失败上报，不让自检程序以未处理异常退出。
            Check("后端定位检查可以在本地目录中完成", false, $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { }
        }
    }

    private static string Touch(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, "stub");
        return path;
    }

    /// <summary>
    /// 二维码渲染必须能被解回原内容：只查像素非空是不够的，
    /// 画歪、翻转或颜色通道搞错都会让手机扫不出或扫出别的东西。
    /// </summary>
    private static void QrRenderTests()
    {
        const string url = "https://bodian-oia.kuwo.cn/bodian/download.html?pageName=login_pc&pt=3&id=0123456789abcdef";
        var image = new Dianbo.Infrastructure.Auth.ZxingQrCodeRenderer().Render(url, 240);
        Equal("二维码宽度", 240, image.Width);
        Equal("二维码高度", 240, image.Height);
        Equal("二维码像素长度（BGRA32）", 240 * 240 * 4, image.Pixels.Length);

        try
        {
            var luminance = new ZXing.RGBLuminanceSource(
                image.Pixels, image.Width, image.Height, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new ZXing.BarcodeReaderGeneric
            {
                AutoRotate = false,
                Options = new ZXing.Common.DecodingOptions { TryHarder = true }
            };
            var decoded = reader.Decode(luminance);
            Check("二维码能被解码回原内容", decoded?.Text == url, decoded?.Text ?? "（解码失败）");
        }
        catch (Exception exception)
        {
            Check("二维码能被解码回原内容", false, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void StorageTests()
    {
        var standardRoot = AppPaths.GetStandardDefaultRoot();
        Check("标准默认数据目录不为空", !string.IsNullOrWhiteSpace(standardRoot));
        Check("标准默认数据目录以DianboClient结尾", standardRoot.EndsWith("DianboClient", StringComparison.OrdinalIgnoreCase));

        var originalConfig = Environment.GetEnvironmentVariable("DIANBO_CONFIG_DIRECTORY");
        var isolatedConfig = Path.Combine(Path.GetTempPath(), "dianbo-config-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DIANBO_CONFIG_DIRECTORY", isolatedConfig);
        var originalCustom = AppPaths.LoadCustomRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "dianbo-storage-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 验证未设置自定义目录时的行为
            AppPaths.SaveCustomRoot(null);
            Equal("清除后未设置自定义目录", null, AppPaths.LoadCustomRoot());
            Equal("清除后默认路径为标准路径", standardRoot, AppPaths.ResolveDefaultRoot());
            var standardPaths = new AppPaths();
            Equal("默认AppPaths使用标准路径", standardRoot, standardPaths.Root);
            Check("默认AppPaths非自定义", !standardPaths.IsCustomRoot);

            // 验证保存自定义目录
            AppPaths.SaveCustomRoot(tempRoot);
            Equal("成功读取保存的自定义目录", tempRoot, AppPaths.LoadCustomRoot());
            Equal("ResolveDefaultRoot返回自定义目录", tempRoot, AppPaths.ResolveDefaultRoot());
            var resolvedCustomPaths = new AppPaths();
            Equal("AppPaths自动解析自定义目录", tempRoot, resolvedCustomPaths.Root);
            Check("AppPaths标记为自定义目录", resolvedCustomPaths.IsCustomRoot);

            // 验证显式传参自定义 root
            var customPaths = new AppPaths(tempRoot);
            Equal("自定义 root 保持一致", tempRoot, customPaths.Root);
            Check("显式传参识别为自定义", customPaths.IsCustomRoot);

            // 验证 EnsureCreated 创建所需子目录
            customPaths.EnsureCreated();
            Check("根目录创建成功", Directory.Exists(tempRoot));
            Check("日志目录创建成功", Directory.Exists(Path.GetDirectoryName(customPaths.LogPath)));
            Check("封面缓存目录创建成功", Directory.Exists(customPaths.CoverCachePath));
            // 验证 SettingsStore 保存与加载非默认音量（如 85）不丢失
            var settingsFile = Path.Combine(tempRoot, "settings.json");
            var settingsStore = new SettingsStore(settingsFile);
            var defaultSettings = settingsStore.Load();
            Equal("默认初始音量为 60", 60, defaultSettings.Volume);
            Check("默认开机自启为 false", !defaultSettings.AutoStartOnBoot);
            Check("默认开机自启静默为 true", defaultSettings.SilentAutoStart);
            Check("默认启动自动播放为 false", !defaultSettings.AutoPlayOnLaunch);
            Equal("默认关闭行为为 MinimizeToTray", "MinimizeToTray", defaultSettings.CloseBehavior);
            Equal("默认主题为 Default", "Default", defaultSettings.Theme);

            defaultSettings.Volume = 85;
            defaultSettings.AutoStartOnBoot = true;
            defaultSettings.SilentAutoStart = false;
            defaultSettings.AutoPlayOnLaunch = true;
            defaultSettings.CloseBehavior = "Exit";
            defaultSettings.Theme = "Dark";
            settingsStore.Save(defaultSettings);

            var loadedSettings = settingsStore.Load();
            Equal("重新加载后保存的音量 85 保持一致", 85, loadedSettings.Volume);
            Check("保存后开机自启为 true", loadedSettings.AutoStartOnBoot);
            Check("保存后开机自启静默为 false", !loadedSettings.SilentAutoStart);
            Check("保存后启动自动播放为 true", loadedSettings.AutoPlayOnLaunch);
            Equal("保存后关闭行为为 Exit", "Exit", loadedSettings.CloseBehavior);
            Equal("保存后主题为 Dark", "Dark", loadedSettings.Theme);
        }
        catch (Exception exception)
        {
            Check("存储自检通过", false, $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            try { AppPaths.SaveCustomRoot(originalCustom); } catch { }
            Environment.SetEnvironmentVariable("DIANBO_CONFIG_DIRECTORY", originalConfig);
            try { Directory.Delete(isolatedConfig, true); } catch { }
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static void UserProfileTests()
    {
        // 1. 普通用户状态
        var regularUser = new UserProfile
        {
            Uid = 12345,
            Nickname = "普通听众",
            IsVip = false
        };
        Equal("普通用户会员文本", "普通用户", regularUser.VipStatusText);

        // 2. VIP 到期时间在未来
        var futureDate = DateTimeOffset.Now.AddDays(30);
        var vipUser = new UserProfile
        {
            Uid = 23456,
            Nickname = "VIP音乐人",
            IsVip = true,
            VipExpireDate = futureDate
        };
        Equal("有效VIP会员文本", $"VIP 会员 · {futureDate:yyyy-MM-dd} 到期", vipUser.VipStatusText);

        // 3. VIP 已过期
        var pastDate = DateTimeOffset.Now.AddDays(-1);
        var expiredVip = new UserProfile
        {
            Uid = 34567,
            Nickname = "过期用户",
            IsVip = true,
            VipExpireDate = pastDate
        };
        Equal("过期VIP会员文本", "VIP 已过期", expiredVip.VipStatusText);

        // 4. VIP 无具体到期时间
        var foreverVip = new UserProfile
        {
            Uid = 45678,
            Nickname = "终身用户",
            IsVip = true,
            VipExpireDate = null
        };
        Equal("无到期时间VIP会员文本", "VIP 会员", foreverVip.VipStatusText);

        // 5. 从真实 Kuwo/Bodian 登录响应 JSON 解析 UserProfile
        var sampleJson = """
        {
            "code": 200,
            "data": {
                "id": 43290106,
                "token": "test-token-123",
                "userInfo": {
                    "id": 43290106,
                    "nickname": "weiwei",
                    "headImg": "https://thirdqq.qlogo.cn/ek_qqapp/avatar.jpg",
                    "isVip": 1,
                    "vipType": 1
                },
                "payInfo": {
                    "isVip": 1,
                    "isVipBoolean": true,
                    "vipType": 1,
                    "expireDate": 1909551293234
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(sampleJson);
        var parsed = AuthService.ParseUserProfile(doc.RootElement, 43290106);
        Check("解析结果非空", parsed is not null);
        Equal("解析UID", 43290106L, parsed!.Uid);
        Equal("解析昵称", "weiwei", parsed.Nickname);
        Equal("解析头像", "https://thirdqq.qlogo.cn/ek_qqapp/avatar.jpg", parsed.AvatarUrl);
        Check("解析VIP状态为真", parsed.IsVip);
        Check("解析VIP到期时间非空", parsed.VipExpireDate.HasValue);
        Equal("解析VIP到期时间年份", 2030, parsed.VipExpireDate!.Value.Year);
        Equal("解析VIP会员说明包含到期日", "VIP 会员 · 2030-07-06 到期", parsed.VipStatusText);
    }

    private static void PlaylistAndHistoryTests()
    {
        // 1. Playlist 模型
        var pl = new Playlist
        {
            Id = 82553201,
            Name = "我喜欢的音乐",
            CoverUrl = "https://img4.kuwo.cn/star/albumcover/test.jpg",
            MusicCount = 67,
            IsFond = true,
            CreatorName = "weiwei",
            CreatorId = 43290106
        };
        Equal("歌单ID", 82553201L, pl.Id);
        Equal("歌单名称", "我喜欢的音乐", pl.Name);
        Equal("我喜欢标识", true, pl.IsFond);
        Equal("歌单副标题", "67 首歌曲 · weiwei", pl.DisplaySubtitle);

        // 2. HistoryService 本地历史存储、去重与数量限制
        var tempHistFile = Path.Combine(Path.GetTempPath(), "dianbo-hist-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var history = new HistoryService(tempHistFile, enableLegacyMigration: false);
            var initial = history.GetRecentSongsAsync(50).GetAwaiter().GetResult();
            Equal("初始历史为空", 0, initial.Count);

            var s1 = new Song { Id = 101, Name = "Song 1", Artist = "Artist 1" };
            var s2 = new Song { Id = 102, Name = "Song 2", Artist = "Artist 2" };
            var s3 = new Song { Id = 103, Name = "Song 3", Artist = "Artist 3" };

            history.AddRecentSongAsync(s1).GetAwaiter().GetResult();
            history.AddRecentSongAsync(s2).GetAwaiter().GetResult();
            var list1 = history.GetRecentSongsAsync().GetAwaiter().GetResult();
            Equal("添加后历史数量", 2, list1.Count);
            Equal("最新播放位于首位", 102L, list1[0].Id);
            Equal("上一首位于次位", 101L, list1[1].Id);

            // 再次播放 s1，应当去重并提升至首位
            history.AddRecentSongAsync(s1).GetAwaiter().GetResult();
            var list2 = history.GetRecentSongsAsync().GetAwaiter().GetResult();
            Equal("重复添加去重后数量不变", 2, list2.Count);
            Equal("重新播放提升至首位", 101L, list2[0].Id);
            Equal("次位为另一首", 102L, list2[1].Id);

            // 清空历史
            history.ClearRecentSongsAsync().GetAwaiter().GetResult();
            var list3 = history.GetRecentSongsAsync().GetAwaiter().GetResult();
            Equal("清空后历史为空", 0, list3.Count);
        }
        finally
        {
            try { if (File.Exists(tempHistFile)) File.Delete(tempHistFile); } catch { }
        }

        // 3. 真实 Fond 接口 JSON 解析
        var fondJson = """
        {
            "code": 200,
            "msg": "success",
            "data": {
                "id": 82553201,
                "name": "weiwei喜欢的音乐",
                "pic": "https://img4.kuwo.cn/star/albumcover/test.jpg",
                "creatorId": 43290106,
                "creatorName": "weiwei",
                "musicCount": 67,
                "isFond": 1
            }
        }
        """;
        using var fondDoc = JsonDocument.Parse(fondJson);
        var fondData = fondDoc.RootElement.GetProperty("data");
        Equal("解析我喜欢ID", 82553201L, fondData.GetProperty("id").GetInt64());
        Equal("解析我喜欢数量", 67, fondData.GetProperty("musicCount").GetInt32());

        // 4. 真实 userCreate 接口 JSON 解析
        var ucJson = """
        {
            "code": 200,
            "msg": "success",
            "data": {
                "playLists": [
                    {
                        "id": 87881983,
                        "name": "26-05-28",
                        "pic": "https://img4.kuwo.cn/star/albumcover/test2.jpg",
                        "creatorId": 43290106,
                        "creatorName": "weiwei",
                        "musicCount": 202,
                        "isFond": 0
                    }
                ]
            }
        }
        """;
        using var ucDoc = JsonDocument.Parse(ucJson);
        var ucLists = ucDoc.RootElement.GetProperty("data").GetProperty("playLists");
        Equal("解析自建歌单数量", 1, ucLists.GetArrayLength());
        Equal("解析自建歌单第一项ID", 87881983L, ucLists[0].GetProperty("id").GetInt64());

        // 5. 真实 musicList (source=5) 分页响应 JSON 解析
        var mlJson = """
        {
            "code": 200,
            "msg": "success",
            "data": {
                "pageNum": 1,
                "pageSize": 10,
                "total": 67,
                "list": [
                    {
                        "id": 123456,
                        "name": "海阔天空",
                        "artist": "Beyond",
                        "album": "乐与怒",
                        "albumId": 789,
                        "duration": 324,
                        "albumPic": "https://img4.kuwo.cn/cover.jpg"
                    }
                ]
            }
        }
        """;
        using var mlDoc = JsonDocument.Parse(mlJson);
        var mlData = mlDoc.RootElement.GetProperty("data");
        Equal("解析歌曲列表总数", 67, mlData.GetProperty("total").GetInt32());
        var songsList = mlData.GetProperty("list");
        Equal("解析歌曲列表当前页项数", 1, songsList.GetArrayLength());
        Equal("解析歌曲第一项ID", 123456L, songsList[0].GetProperty("id").GetInt64());
        Equal("解析歌曲第一项名称", "海阔天空", songsList[0].GetProperty("name").GetString());

        // 6. 推荐歌单模型与播放量/角标格式化验证
        var recPl = new Playlist
        {
            Id = 3198201626L,
            Name = "抱着青峰 复习苏打绿",
            CreatorName = "覃妮儿",
            MusicCount = 43,
            PlayCount = 32680,
            SourceType = 4
        };
        Equal("推荐歌单角标格式化", "3.3万次播放", recPl.PlayCountBadge);
        Equal("推荐歌单副标题", "43 首歌曲 · 覃妮儿", recPl.DisplaySubtitle);
        Equal("播放量格式化亿级", "1.2亿", Playlist.FormatPlayCount(120_000_000));
        Equal("播放量格式化万级", "32.5万", Playlist.FormatPlayCount(325_000));

        // 7. 真实 finds/module 歌单响应 JSON 解析
        var modJson = """
        {
            "code": 200,
            "data": {
                "moduleId": 15,
                "moduleName": "迎夏的备考小调",
                "songList": [
                    {
                        "id": 3198201626,
                        "name": "抱着青峰 复习苏打绿",
                        "pic": "http://img1.kwcdn.kuwo.cn/star/userpl2015/test.jpg",
                        "creatorName": "覃妮儿",
                        "musicCount": 43,
                        "playNum": 32680,
                        "sourceType": 4
                    }
                ]
            }
        }
        """;
        using var modDoc = JsonDocument.Parse(modJson);
        var modSongList = modDoc.RootElement.GetProperty("data").GetProperty("songList");
        Equal("解析发现页模块歌单列表项数", 1, modSongList.GetArrayLength());
        Equal("解析模块歌单ID", 3198201626L, modSongList[0].GetProperty("id").GetInt64());
        Equal("解析模块歌单播放量", 32680L, modSongList[0].GetProperty("playNum").GetInt64());
        Equal("解析模块歌单源类型", 4, modSongList[0].GetProperty("sourceType").GetInt32());

        // 8. 歌单分页与完整加载验证（防止歌单尾部歌曲丢失）
        var mockPagedApi = new PagedTestApiClient(totalPages: 3, songsPerPage: 100, lastPageSongs: 13);
        var allLoaded = mockPagedApi.GetAllPlaylistSongsAsync(82553544L, CancellationToken.None).GetAwaiter().GetResult();
        Equal("完整歌单拉取总数", 213, allLoaded.Count);
        Equal("第一首歌ID", 1L, allLoaded[0].Id);
        Equal("最后一首歌ID", 213L, allLoaded[^1].Id);
    }

    private static void QualityAndAudioSourceTests()
    {
        // 1. AudioSource DisplayQuality 格式化验证（不展示具体码率与体积，保持界面简洁）
        var flacWithMb = new AudioSource
        {
            SongId = 27007915,
            Url = "https://bd-er.kuwo.cn/sample.flac",
            Format = "flac",
            Bitrate = 2000,
            SizeText = "42.52Mb"
        };
        Equal("FLAC格式化", "无损品质 · FLAC", flacWithMb.DisplayQuality);

        var flacNoSize = new AudioSource
        {
            SongId = 27007915,
            Url = "https://bd-er.kuwo.cn/sample.flac",
            Format = "flac",
            Bitrate = 2000
        };
        Equal("FLAC无大小格式化", "无损品质 · FLAC", flacNoSize.DisplayQuality);

        var mp3_320 = new AudioSource
        {
            SongId = 27007915,
            Url = "https://bd-er.kuwo.cn/sample.mp3",
            Format = "mp3",
            Bitrate = 320,
            SizeText = "8.45Mb"
        };
        Equal("320k MP3格式化", "极高品质 · MP3", mp3_320.DisplayQuality);

        var mp3_128 = new AudioSource
        {
            SongId = 27007915,
            Url = "https://bd-er.kuwo.cn/sample.mp3",
            Format = "mp3",
            Bitrate = 128,
            SizeText = "3.38Mb"
        };
        Equal("128k MP3格式化", "标准品质 · MP3", mp3_128.DisplayQuality);

        // 2. AppSettings PreferredQuality 默认值与持久化序列化
        var settingsDefault = new AppSettings();
        Equal("AppSettings默认音质", "2000kflac", settingsDefault.PreferredQuality);
        Check("AppSettings默认自动跳转正在播放页", settingsDefault.AutoNavigateToNowPlayingOnPlay);

        var json = JsonSerializer.Serialize(new AppSettings { PreferredQuality = "320kmp3", AutoNavigateToNowPlayingOnPlay = false });
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);
        Check("AppSettings反序列化非空", deserialized is not null);
        Equal("AppSettings反序列化音质", "320kmp3", deserialized!.PreferredQuality);
        Check("AppSettings反序列化自动跳转选项", !deserialized.AutoNavigateToNowPlayingOnPlay);

        // 3. PlaybackSnapshot 携带 AudioSource
        var snapshot = PlaybackSnapshot.Initial with { AudioSource = flacWithMb };
        Check("快照携带音频源非空", snapshot.AudioSource is not null);
        Equal("快照音频源音质", "无损品质 · FLAC", snapshot.AudioSource!.DisplayQuality);

        var nextSnapshot = snapshot with { Position = TimeSpan.FromSeconds(30) };
        Check("衍生快照继承音频源", nextSnapshot.AudioSource is not null);
        Equal("衍生快照音频源一致", 2000, nextSnapshot.AudioSource!.Bitrate);
    }

    private static void FavoriteServiceTests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DianboTestFavorites_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var favPath = Path.Combine(tempDir, "favorites.json");

        try
        {
            var mockApi = new DummyApiClient();
            var mockCreds = new DummyAuthCredentialSource();
            var favService = new FavoriteService(favPath, mockApi, mockCreds);

            // 1. 初始状态
            Check("初始状态未收藏", !favService.IsFavorite(1001));

            // 2. 添加收藏
            var song1 = new Song { Id = 1001, Name = "歌1", Artist = "歌手1", Duration = TimeSpan.FromSeconds(200) };
            var song2 = new Song { Id = 1002, Name = "歌2", Artist = "歌手2", Duration = TimeSpan.FromSeconds(180) };

            var changedCount = 0;
            favService.FavoritesChanged += (_, _) => changedCount++;

            var addRes = favService.AddFavoriteAsync(song1).GetAwaiter().GetResult();
            Check("添加收藏返回成功", addRes);
            Check("添加后IsFavorite为真", favService.IsFavorite(1001));
            Equal("触发FavoritesChanged事件", 1, changedCount);

            // 3. 再次添加第2首
            favService.AddFavoriteAsync(song2).GetAwaiter().GetResult();
            Check("第2首IsFavorite为真", favService.IsFavorite(1002));
            var songs = favService.GetFavoriteSongsAsync().GetAwaiter().GetResult();
            Equal("收藏歌曲数量为2", 2, songs.Count);
            Equal("最新收藏排在前面", 1002L, songs[0].Id);

            // 4. 切换收藏状态 (Toggle)
            var toggledOff = favService.ToggleFavoriteAsync(song1).GetAwaiter().GetResult();
            Check("Toggle已有歌曲返回false", !toggledOff);
            Check("Toggle后IsFavorite为false", !favService.IsFavorite(1001));

            var toggledOn = favService.ToggleFavoriteAsync(song1).GetAwaiter().GetResult();
            Check("Toggle未收藏歌曲返回true", toggledOn);
            Check("Toggle后IsFavorite为true", favService.IsFavorite(1001));

            // 5. 移除收藏
            var removeRes = favService.RemoveFavoriteAsync(1001).GetAwaiter().GetResult();
            Check("移除收藏返回成功", removeRes);
            Check("移除后IsFavorite为false", !favService.IsFavorite(1001));

            // 6. 重新实例化，验证本地持久化已写入并恢复
            var favService2 = new FavoriteService(favPath, mockApi, mockCreds);
            Check("持久化恢复-未移除的仍存在", favService2.IsFavorite(1002));
            Check("持久化恢复-已移除的不存在", !favService2.IsFavorite(1001));
            var restoredSongs = favService2.GetFavoriteSongsAsync().GetAwaiter().GetResult();
            Equal("持久化恢复歌曲数", 1, restoredSongs.Count);
            Equal("持久化恢复歌曲名", "歌2", restoredSongs[0].Name);
            // 7. 验证 API 签名算法 (ComputeSign) 与已知真实向量一致
            var testParams = new Dictionary<string, string?>
            {
                ["uid"] = "43290106",
                ["token"] = "1a4979a21e4eaf80c58157dcfee8392a",
                ["timestamp"] = "1790675572255"
            };
            var testBody = "{\"playListId\":82553201,\"musicIdList\":[550579270]}";
            var sign = BodianApiClient.ComputeSign("/api/service/playlist/music", testParams, testBody);
            Equal("波点API签名计算", "76a5e578000f9f3de6505bf96f1e848f", sign);

            // 8. 验证 SyncAsync 合并：云端添加未成功的本地待同步歌曲不会被冲掉
            var mockApiWithServerSongs = new SyncTestApiClient();
            var favServiceSync = new FavoriteService(favPath, mockApiWithServerSongs, mockCreds);
            // 本地收藏一首新歌
            var localOnlySong = new Song { Id = 9999, Name = "本地新增歌", Artist = "歌手99" };
            favServiceSync.AddFavoriteAsync(localOnlySong).GetAwaiter().GetResult();
            Check("本地新增歌已收藏", favServiceSync.IsFavorite(9999));
            // 此时调用 SyncAsync（云端返回歌曲并不包含 9999）
            favServiceSync.SyncAsync().GetAwaiter().GetResult();
            Check("Sync后本地新增歌依然保留不被清空", favServiceSync.IsFavorite(9999));
            Check("Sync后云端歌曲也已合并入库", favServiceSync.IsFavorite(8888));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void UserPlaylistServiceTests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DianboTestPlaylists_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var plPath = Path.Combine(tempDir, "playlists.json");

        try
        {
            var mockApi = new DummyApiClient();
            var mockCreds = new DummyAuthCredentialSource();
            var service = new UserPlaylistService(plPath, mockApi, mockCreds);

            // 1. 初始状态
            var initialList = service.GetPlaylistsAsync().GetAwaiter().GetResult();
            Equal("初始自建歌单为空", 0, initialList.Count);

            // 2. 创建歌单
            var changedCount = 0;
            service.PlaylistsChanged += (_, _) => changedCount++;

            var pl1 = service.CreatePlaylistAsync("我的宝藏歌单").GetAwaiter().GetResult();
            Check("创建歌单返回有效对象", pl1 != null && pl1.Id > 0);
            Equal("创建歌单名称正确", "我的宝藏歌单", pl1!.Name);
            Equal("创建歌单触发PlaylistsChanged", 1, changedCount);

            var listAfterCreate = service.GetPlaylistsAsync().GetAwaiter().GetResult();
            Equal("创建后歌单总数为1", 1, listAfterCreate.Count);
            Equal("创建后列表中歌单ID匹配", pl1.Id, listAfterCreate[0].Id);

            // 3. 向歌单添加歌曲
            var songA = new Song { Id = 2001, Name = "歌曲A", Artist = "歌手A", CoverUrl = "http://cover/a.jpg" };
            var songB = new Song { Id = 2002, Name = "歌曲B", Artist = "歌手B" };

            var addARes = service.AddSongToPlaylistAsync(pl1.Id, songA).GetAwaiter().GetResult();
            Check("添加歌曲A返回true", addARes);
            var addBRes = service.AddSongToPlaylistAsync(pl1.Id, songB).GetAwaiter().GetResult();
            Check("添加歌曲B返回true", addBRes);

            var songsInPl = service.GetPlaylistSongsAsync(pl1.Id).GetAwaiter().GetResult();
            Equal("歌单歌曲数量为2", 2, songsInPl.Count);
            Equal("第一首歌曲名正确", "歌曲A", songsInPl[0].Name);

            // 4. 从歌单移除歌曲
            var removeARes = service.RemoveSongFromPlaylistAsync(pl1.Id, 2001).GetAwaiter().GetResult();
            Check("移除歌曲A返回true", removeARes);
            var songsAfterRemove = service.GetPlaylistSongsAsync(pl1.Id).GetAwaiter().GetResult();
            Equal("移除后歌曲数量为1", 1, songsAfterRemove.Count);
            Equal("剩余歌曲为B", 2002L, songsAfterRemove[0].Id);

            // 5. 本地持久化恢复验证
            var service2 = new UserPlaylistService(plPath, mockApi, mockCreds);
            var restoredPlaylists = service2.GetPlaylistsAsync().GetAwaiter().GetResult();
            Equal("新实例恢复歌单总数", 1, restoredPlaylists.Count);
            Equal("新实例恢复歌单名称", "我的宝藏歌单", restoredPlaylists[0].Name);

            var restoredSongs = service2.GetPlaylistSongsAsync(pl1.Id).GetAwaiter().GetResult();
            Equal("新实例恢复歌单歌曲数", 1, restoredSongs.Count);
            Equal("新实例恢复歌曲ID", 2002L, restoredSongs[0].Id);

            // 6. 删除歌单
            var deleteRes = service2.DeletePlaylistAsync(pl1.Id).GetAwaiter().GetResult();
            Check("删除歌单返回true", deleteRes);
            var listAfterDelete = service2.GetPlaylistsAsync().GetAwaiter().GetResult();
            Equal("删除后歌单列表为空", 0, listAfterDelete.Count);

            // 7. 云端删除成功后读取服务器最新列表
            var cloudApi = new PlaylistFilterTestApiClient();
            var cloudCreds = new DummyAuthCredentialSourceWithSession();
            var service3 = new UserPlaylistService(plPath, cloudApi, cloudCreds);
            var cloudPlaylists = service3.GetPlaylistsAsync().GetAwaiter().GetResult();
            Equal("云端初始2个歌单", 2, cloudPlaylists.Count);

            // 删除云端歌单 7777
            service3.DeletePlaylistAsync(7777).GetAwaiter().GetResult();
            var cloudPlaylistsAfterDel = service3.GetPlaylistsAsync().GetAwaiter().GetResult();
            Equal("删除后仅剩1个歌单", 1, cloudPlaylistsAfterDel.Count);
            Equal("剩余歌单为8888", 8888L, cloudPlaylistsAfterDel[0].Id);

            // 重新实例化仍从云端确认删除结果
            var service4 = new UserPlaylistService(plPath, cloudApi, cloudCreds);
            var cloudPlaylistsRestored = service4.GetPlaylistsAsync().GetAwaiter().GetResult();
            Equal("新实例读取云端删除后的列表", 1, cloudPlaylistsRestored.Count);
            Equal("新实例中剩余歌单为8888", 8888L, cloudPlaylistsRestored[0].Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class DummyAuthCredentialSourceWithSession : Dianbo.Infrastructure.Api.IAuthCredentialSource
    {
        public Session? Session => new Session { Uid = 43290106, UserName = "测试用户", Token = "test-token", DevId = "test-dev-id" };
        public string DevId => "test-dev-id";
    }

    private sealed class PlaylistFilterTestApiClient : IBodianApiClient
    {
        private readonly List<Playlist> _playlists = [new() { Id = 7777, Name = "云端歌单A" }, new() { Id = 8888, Name = "云端歌单B" }];
        public Task<IReadOnlyList<Song>> SearchAsync(string keyword, int pageIndex, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<bool> CheckPlayRightAsync(long songId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<AudioSource> ResolveAudioSourceAsync(long songId, string bitrate, CancellationToken cancellationToken) => Task.FromResult(new AudioSource { SongId = songId, Url = "test", Format = "flac", Bitrate = 2000 });
        public Task<string?> GetLyricsAsync(long songId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<Playlist?> GetFondPlaylistAsync(CancellationToken cancellationToken) => Task.FromResult<Playlist?>(null);
        public Task<IReadOnlyList<Playlist>> GetUserPlaylistsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Playlist>>(_playlists.ToList());
        public Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(long playlistId, int pageIndex, int pageSize, CancellationToken cancellationToken, int sourceType = 5) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<IReadOnlyList<Song>> GetAllPlaylistSongsAsync(long playlistId, CancellationToken cancellationToken, int sourceType = 5) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<IReadOnlyList<Song>> GetRecommendSongsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<IReadOnlyList<Playlist>> GetRecommendPlaylistsAsync(int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Playlist>>([]);
        public Task<bool> RecordBehaviorAsync(long songId, string operation, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> AddPlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DeletePlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Playlist?> CreatePlaylistAsync(string name, CancellationToken cancellationToken) => Task.FromResult<Playlist?>(new Playlist { Id = 9999, Name = name });
        public Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken) => Task.FromResult(_playlists.RemoveAll(p => p.Id == playlistId) > 0);
    }

    private sealed class SyncTestApiClient : IBodianApiClient
    {
        public Task<IReadOnlyList<Song>> SearchAsync(string keyword, int pageIndex, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<bool> CheckPlayRightAsync(long songId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<AudioSource> ResolveAudioSourceAsync(long songId, string bitrate, CancellationToken cancellationToken) => Task.FromResult(new AudioSource { SongId = songId, Url = "test", Format = "flac", Bitrate = 2000 });
        public Task<string?> GetLyricsAsync(long songId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<Playlist?> GetFondPlaylistAsync(CancellationToken cancellationToken) => Task.FromResult<Playlist?>(new Playlist { Id = 82553201, Name = "我喜欢的音乐", MusicCount = 1 });
        public Task<IReadOnlyList<Playlist>> GetUserPlaylistsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Playlist>>([]);
        public Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(long playlistId, int pageIndex, int pageSize, CancellationToken cancellationToken, int sourceType = 5) =>
            Task.FromResult<IReadOnlyList<Song>>([new Song { Id = 8888, Name = "云端歌", Artist = "云端歌手" }]);
        public Task<IReadOnlyList<Song>> GetAllPlaylistSongsAsync(long playlistId, CancellationToken cancellationToken, int sourceType = 5) =>
            Task.FromResult<IReadOnlyList<Song>>([new Song { Id = 8888, Name = "云端歌", Artist = "云端歌手" }]);
        public Task<IReadOnlyList<Song>> GetRecommendSongsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<IReadOnlyList<Playlist>> GetRecommendPlaylistsAsync(int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Playlist>>([]);
        public Task<bool> RecordBehaviorAsync(long songId, string operation, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> AddPlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> DeletePlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Playlist?> CreatePlaylistAsync(string name, CancellationToken cancellationToken) => Task.FromResult<Playlist?>(null);
        public Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class PagedTestApiClient : IBodianApiClient
    {
        private readonly int _totalPages;
        private readonly int _songsPerPage;
        private readonly int _lastPageSongs;

        public PagedTestApiClient(int totalPages, int songsPerPage, int lastPageSongs)
        {
            _totalPages = totalPages;
            _songsPerPage = songsPerPage;
            _lastPageSongs = lastPageSongs;
        }

        public Task<IReadOnlyList<Song>> SearchAsync(string keyword, int pageIndex, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<bool> CheckPlayRightAsync(long songId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<AudioSource> ResolveAudioSourceAsync(long songId, string bitrate, CancellationToken cancellationToken) => Task.FromResult(new AudioSource { SongId = songId, Url = "test", Format = "flac", Bitrate = 2000 });
        public Task<string?> GetLyricsAsync(long songId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<Playlist?> GetFondPlaylistAsync(CancellationToken cancellationToken) => Task.FromResult<Playlist?>(null);
        public Task<IReadOnlyList<Playlist>> GetUserPlaylistsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Playlist>>([]);
        public Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(long playlistId, int pageIndex, int pageSize, CancellationToken cancellationToken, int sourceType = 5)
        {
            if (pageIndex >= _totalPages) return Task.FromResult<IReadOnlyList<Song>>([]);
            var count = (pageIndex == _totalPages - 1) ? _lastPageSongs : _songsPerPage;
            var startId = pageIndex * _songsPerPage + 1;
            var list = Enumerable.Range(startId, count).Select(i => new Song { Id = i, Name = $"Song {i}", Artist = "Artist" }).ToList();
            return Task.FromResult<IReadOnlyList<Song>>(list);
        }
        public async Task<IReadOnlyList<Song>> GetAllPlaylistSongsAsync(long playlistId, CancellationToken cancellationToken, int sourceType = 5)
        {
            var result = new List<Song>();
            int page = 0;
            while (page < _totalPages)
            {
                var pageSongs = await GetPlaylistSongsAsync(playlistId, page, 100, cancellationToken, sourceType);
                if (pageSongs.Count == 0) break;
                result.AddRange(pageSongs);
                page++;
            }
            return result;
        }
        public Task<IReadOnlyList<Song>> GetRecommendSongsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<IReadOnlyList<Playlist>> GetRecommendPlaylistsAsync(int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Playlist>>([]);
        public Task<bool> RecordBehaviorAsync(long songId, string operation, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> AddPlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DeletePlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Playlist?> CreatePlaylistAsync(string name, CancellationToken cancellationToken) => Task.FromResult<Playlist?>(null);
        public Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class DummyApiClient : IBodianApiClient
    {
        public Func<IReadOnlyList<Song>>? Recommendations { get; set; }
        public int RecommendationRequests { get; private set; }
        public Task<IReadOnlyList<Song>> SearchAsync(string keyword, int pageIndex, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<bool> CheckPlayRightAsync(long songId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<AudioSource> ResolveAudioSourceAsync(long songId, string bitrate, CancellationToken cancellationToken) => Task.FromResult(new AudioSource { SongId = songId, Url = "test", Format = "flac", Bitrate = 2000 });
        public Task<string?> GetLyricsAsync(long songId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<Playlist?> GetFondPlaylistAsync(CancellationToken cancellationToken) => Task.FromResult<Playlist?>(null);
        public Task<IReadOnlyList<Playlist>> GetUserPlaylistsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Playlist>>([]);
        public Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(long playlistId, int pageIndex, int pageSize, CancellationToken cancellationToken, int sourceType = 5) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<IReadOnlyList<Song>> GetAllPlaylistSongsAsync(long playlistId, CancellationToken cancellationToken, int sourceType = 5) => Task.FromResult<IReadOnlyList<Song>>([]);
        public Task<IReadOnlyList<Song>> GetRecommendSongsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken)
        {
            RecommendationRequests++;
            return Task.FromResult(Recommendations?.Invoke() ?? (IReadOnlyList<Song>)[]);
        }
        public Task<IReadOnlyList<Playlist>> GetRecommendPlaylistsAsync(int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Playlist>>([]);
        public Task<bool> RecordBehaviorAsync(long songId, string operation, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> AddPlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DeletePlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Playlist?> CreatePlaylistAsync(string name, CancellationToken cancellationToken) => Task.FromResult<Playlist?>(null);
        public Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class DummyAuthCredentialSource : Dianbo.Infrastructure.Api.IAuthCredentialSource
    {
        public Session? Session => null;
        public string DevId => "test-dev-id";
    }

    private sealed class FakePlaybackBackend : IPlaybackBackend
    {
        public bool IsRunning { get; private set; } = true;
        public bool IsConfigured => true;
        public string? LastError => null;
        public string? LastLoadedUrl { get; private set; }
        public int LoadCount { get; private set; }
        public event EventHandler<BackendExitInfo>? Exited { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task LoadAsync(string url, long generation, long songId, CancellationToken cancellationToken)
        {
            LoadCount++;
            LastLoadedUrl = url;
            return Task.CompletedTask;
        }
        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopMediaAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<PlaybackSnapshot> ObserveAsync(long generation, long songId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new PlaybackSnapshot { State = PlaybackState.Playing, Generation = generation, SongId = songId };
            await Task.Yield();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void RecommendationRadioTests()
    {
        var nextId = 200L;
        var api = new DummyApiClient
        {
            Recommendations = () => [new Song { Id = ++nextId, Name = $"推荐 {nextId}", Artist = "测试歌手" }]
        };
        var backend = new FakePlaybackBackend();
        var coordinator = new Dianbo.Core.Playback.PlaybackCoordinator(api, backend);
        coordinator.PlayMode = PlaybackMode.RepeatOne;
        coordinator.StartRecommendationRadioAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check("随机单曲已启动", coordinator.IsRecommendationRadio);
        Equal("随机单曲不改变播放模式", PlaybackMode.RepeatOne, coordinator.PlayMode);
        var firstId = coordinator.CurrentSong?.Id;
        var initialLoads = backend.LoadCount;
        var eof = typeof(Dianbo.Core.Playback.PlaybackCoordinator).GetMethod("OnEndOfFileAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ((Task)eof.Invoke(coordinator, null)!).GetAwaiter().GetResult();
        Equal("随机单曲单曲循环自然结束重播当前歌曲", firstId, coordinator.CurrentSong?.Id);
        Equal("单曲循环自然结束不请求新推荐", 1, api.RecommendationRequests);
        Equal("单曲循环自然结束重新加载音频", initialLoads + 1, backend.LoadCount);
        for (var i = 0; i < 5; i++)
            coordinator.NextAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal("每次下一曲请求新推荐", 6, api.RecommendationRequests);
        Equal("推荐历史保留已听歌曲", 6, coordinator.Queue.Count);
        var latestId = coordinator.CurrentSong?.Id;
        coordinator.PreviousAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check("随机单曲上一首返回历史", coordinator.CurrentSong?.Id != latestId);
        coordinator.NextAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check("回退后下一首仍取新推荐", coordinator.CurrentSong?.Id != latestId && coordinator.CurrentSong?.Id != firstId);
        Equal("回退后下一首再次请求接口", 7, api.RecommendationRequests);
        coordinator.PlayQueueAsync([new Song { Id = 999, Name = "普通队列", Artist = "测试歌手" }], 0, CancellationToken.None).GetAwaiter().GetResult();
        Check("播放普通队列退出随机单曲", !coordinator.IsRecommendationRadio);
    }

    private static void QueueAndPlayModeTests()
    {
        var api = new DummyApiClient();
        var backend = new FakePlaybackBackend();
        var coordinator = new Dianbo.Core.Playback.PlaybackCoordinator(api, backend);

        var song1 = new Song { Id = 101, Name = "Song A", Artist = "Artist A" };
        var song2 = new Song { Id = 102, Name = "Song B", Artist = "Artist B" };
        var song3 = new Song { Id = 103, Name = "Song C", Artist = "Artist C" };

        var queueChangedCount = 0;
        coordinator.QueueChanged += (_, _) => queueChangedCount++;

        var playModeChangedCount = 0;
        coordinator.PlayModeChanged += (_, _) => playModeChangedCount++;

        // 1. 初始化队列并播放
        coordinator.PlayQueueAsync([song1, song2, song3], 0, CancellationToken.None).GetAwaiter().GetResult();
        Equal("初始队列长度", 3, coordinator.Queue.Count);
        Equal("初始播放索引", 0, coordinator.QueueIndex);
        Equal("当前播放歌曲", 101L, coordinator.CurrentSong?.Id ?? 0);
        Check("QueueChanged 事件触发", queueChangedCount > 0);

        // 2. PlayModeChanged 事件
        coordinator.PlayMode = PlaybackMode.Sequential;
        Equal("切换为顺序播放", PlaybackMode.Sequential, coordinator.PlayMode);
        Check("PlayModeChanged 事件触发", playModeChangedCount > 0);

        // 3. 顺序播放模式：前进
        coordinator.NextAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal("顺序播放下一首索引", 1, coordinator.QueueIndex);
        coordinator.NextAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal("顺序播放最后一首索引", 2, coordinator.QueueIndex);
        // 到达末尾后再次前进：停止并标记 Ended，不循环
        coordinator.NextAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal("顺序播放到末尾停止", PlaybackState.Ended, coordinator.Snapshot.State);

        // 4. 顺序播放模式：后退
        coordinator.PlayAtAsync(0, CancellationToken.None).GetAwaiter().GetResult();
        coordinator.PreviousAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal("顺序播放第一首无法再后退", 0, coordinator.QueueIndex);

        // 5. 列表循环模式：末尾循环到开头，开头循环到末尾
        coordinator.PlayMode = PlaybackMode.RepeatAll;
        coordinator.PlayAtAsync(2, CancellationToken.None).GetAwaiter().GetResult();
        coordinator.NextAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal("列表循环从末尾回到开头", 0, coordinator.QueueIndex);
        coordinator.PreviousAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal("列表循环从开头退到末尾", 2, coordinator.QueueIndex);

        // 6. 随机播放模式
        coordinator.PlayMode = PlaybackMode.Shuffle;
        coordinator.PlayAtAsync(1, CancellationToken.None).GetAwaiter().GetResult();
        coordinator.NextAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check("随机播放挑选了不同歌曲", coordinator.QueueIndex != 1 && coordinator.QueueIndex >= 0 && coordinator.QueueIndex < 3);
        // 上一首应该退回刚才的索引 1
        coordinator.PreviousAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal("随机播放上一首返回历史", 1, coordinator.QueueIndex);

        // 7. 队列动态添加与插入
        var song4 = new Song { Id = 104, Name = "Song D", Artist = "Artist D" };
        var song5 = new Song { Id = 105, Name = "Song E", Artist = "Artist E" };
        coordinator.EnqueueAsync(song4, playNext: true).GetAwaiter().GetResult();
        Equal("下一首插入成功", 104L, coordinator.Queue[coordinator.QueueIndex + 1].Id);

        coordinator.EnqueueAsync(song5, playNext: false).GetAwaiter().GetResult();
        Equal("添加到末尾成功", 105L, coordinator.Queue[^1].Id);

        // 8. 队列移除单项
        var countBefore = coordinator.Queue.Count;
        coordinator.RemoveAtAsync(coordinator.Queue.Count - 1, CancellationToken.None).GetAwaiter().GetResult();
        Equal("移除队列最后一项", countBefore - 1, coordinator.Queue.Count);

        // 9. 清空队列
        coordinator.ClearQueueAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal("清空后队列为空", 0, coordinator.Queue.Count);
        Equal("清空后索引为 -1", -1, coordinator.QueueIndex);

        // 10. QueueStateStore 持久化与恢复测试
        var tempQueueFile = Path.Combine(Path.GetTempPath(), $"dianbo_test_queue_{Guid.NewGuid():N}.json");
        try
        {
            var store = new QueueStateStore(tempQueueFile);
            var emptyRecord = store.Load();
            Equal("初始加载队列为空", 0, emptyRecord.Songs.Count);

            var recordToSave = new QueueStateRecord
            {
                Songs = [song1, song2, song3],
                CurrentIndex = 1,
                PositionSeconds = 42.5,
                DurationSeconds = 180,
                PlayMode = "Shuffle"
            };
            store.Save(recordToSave);

            var loadedRecord = store.Load();
            Equal("加载保存后的歌曲数量", 3, loadedRecord.Songs.Count);
            Equal("加载保存后的当前索引", 1, loadedRecord.CurrentIndex);
            Equal("加载保存后的进度秒数", 42.5, loadedRecord.PositionSeconds);
            Equal("加载保存后的时长秒数", 180.0, loadedRecord.DurationSeconds);
            Equal("加载保存后的播放模式", "Shuffle", loadedRecord.PlayMode);

            // 11. PlaybackCoordinator.RestoreState 恢复待命状态测试
            var restoreBackend = new FakePlaybackBackend();
            restoreBackend.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            var restoreCoordinator = new Dianbo.Core.Playback.PlaybackCoordinator(api, restoreBackend);
            restoreCoordinator.RestoreState(
                loadedRecord.Songs,
                loadedRecord.CurrentIndex,
                TimeSpan.FromSeconds(loadedRecord.PositionSeconds),
                TimeSpan.FromSeconds(loadedRecord.DurationSeconds),
                PlaybackMode.Shuffle);

            Equal("恢复后队列长度", 3, restoreCoordinator.Queue.Count);
            Equal("恢复后当前索引", 1, restoreCoordinator.QueueIndex);
            Equal("恢复后当前歌曲", 102L, restoreCoordinator.CurrentSong?.Id ?? 0);
            Equal("恢复后播放模式", PlaybackMode.Shuffle, restoreCoordinator.PlayMode);
            Equal("恢复后状态为暂停待命", PlaybackState.Paused, restoreCoordinator.Snapshot.State);
            Check("恢复后处于暂停标志", restoreCoordinator.Snapshot.IsPaused);
            Equal("恢复后待命进度重置为0", 0.0, restoreCoordinator.Snapshot.Position.TotalSeconds);
            Equal("恢复后时长一致", 180.0, restoreCoordinator.Snapshot.Duration.TotalSeconds);
            Check("恢复待命时不主动启动后端", !restoreBackend.IsRunning);

            // 12. 点击播放从头开始播放
            restoreCoordinator.TogglePauseAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check("恢复后点击播放启动了后端", restoreBackend.IsRunning);
            Equal("恢复后播放当前歌曲", 102L, restoreCoordinator.CurrentSong?.Id ?? 0);
            Equal("恢复后从头播放", 0.0, restoreCoordinator.Snapshot.Position.TotalSeconds);

            // 13. 模拟启动阶段初始化：在恢复队列前触发 PlayModeChanged 不能将空队列保存至磁盘覆盖已有存档
            var startupStore = new QueueStateStore(tempQueueFile);
            store.Save(recordToSave);
            var startupCoordinator = new Dianbo.Core.Playback.PlaybackCoordinator(api, restoreBackend);
            var queueRestored = false;
            void StartupSaveQueueState()
            {
                if (!queueRestored) return;
                var q = startupCoordinator.Queue;
                if (q.Count == 0) startupStore.Save(new QueueStateRecord());
                else startupStore.Save(new QueueStateRecord { Songs = [.. q] });
            }
            startupCoordinator.PlayModeChanged += (_, _) => StartupSaveQueueState();
            startupCoordinator.PlayMode = PlaybackMode.Sequential;
            Equal("启动初始化切换播放模式不冲掉未恢复队列存档", 3, startupStore.Load().Songs.Count);
            var rec = startupStore.Load();
            startupCoordinator.RestoreState(rec.Songs, rec.CurrentIndex, TimeSpan.Zero, TimeSpan.FromSeconds(rec.DurationSeconds));
            queueRestored = true;
            Equal("恢复完成后队列正常加载", 3, startupCoordinator.Queue.Count);
        }
        finally
        {
            if (File.Exists(tempQueueFile)) File.Delete(tempQueueFile);
        }
    }

    private static void AudioCacheTests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dianbo_cache_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cacheService = new AudioCacheService(tempDir);

            // 1. 初始空缓存检查
            var initialSize = cacheService.GetCacheSizeAsync().GetAwaiter().GetResult();
            Equal("初始缓存文件数", 0, initialSize.FileCount);
            Equal("初始缓存大小", 0L, initialSize.TotalBytes);
            Check("空缓存返回null", cacheService.GetCachedFilePath(101L, "320kmp3") == null);

            // 2. 写入带标准 ID3 / FLAC 头的 mock 音频文件
            var path = Path.Combine(tempDir, "101_320kmp3.mp3");
            var flacPath = Path.Combine(tempDir, "102_2000kflac.flac");

            var mp3Bytes = new byte[1024];
            mp3Bytes[0] = 0x49; mp3Bytes[1] = 0x44; mp3Bytes[2] = 0x33; // "ID3"
            File.WriteAllBytes(path, mp3Bytes);

            var flacBytes = new byte[1024];
            flacBytes[0] = 0x66; flacBytes[1] = 0x4C; flacBytes[2] = 0x61; flacBytes[3] = 0x43; // "fLaC"
            File.WriteAllBytes(flacPath, flacBytes);

            var hitMp3 = cacheService.GetCachedFilePath(101L, "320kmp3");
            Check("写入有效MP3后命中缓存", hitMp3 != null);
            Equal("MP3缓存文件路径一致", Path.GetFullPath(path), Path.GetFullPath(hitMp3!));

            var hitFlac = cacheService.GetCachedFilePath(102L, "2000kflac");
            Check("写入有效FLAC后命中缓存", hitFlac != null);
            Equal("FLAC缓存文件路径一致", Path.GetFullPath(flacPath), Path.GetFullPath(hitFlac!));

            // 3. 校验无效文件头（如破损全0文件）被安全拒绝
            var badPath = Path.Combine(tempDir, "103_320kmp3.mp3");
            File.WriteAllBytes(badPath, new byte[64]);
            Check("无有效音轨头的破损文件被拒绝", cacheService.GetCachedFilePath(103L, "320kmp3") == null);
            File.Delete(badPath);

            // 4. 任意音质回退查找
            var anySource = cacheService.FindAnyCachedFilePath(101L);
            Check("FindAnyCachedFilePath能找到已有缓存", anySource != null);
            Equal("找到的路径一致", Path.GetFullPath(path), Path.GetFullPath(anySource!));

            // 5. 大小统计测试
            var sizeAfter = cacheService.GetCacheSizeAsync().GetAwaiter().GetResult();
            Equal("文件缓存数", 2, sizeAfter.FileCount);
            Equal("文件缓存大小", 2048L, sizeAfter.TotalBytes);

            // 6. 配额淘汰机制 (LRU 基于最后访问时间)
            var path3 = Path.Combine(tempDir, "104_320kmp3.mp3");
            var bigBytes = new byte[600 * 1024]; // 600KB
            bigBytes[0] = 0x49; bigBytes[1] = 0x44; bigBytes[2] = 0x33;
            File.WriteAllBytes(path3, bigBytes);

            var path4 = Path.Combine(tempDir, "105_320kmp3.mp3");
            File.WriteAllBytes(path4, bigBytes);

            File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddHours(-4)); // 最老 (1KB)
            File.SetLastAccessTimeUtc(flacPath, DateTime.UtcNow.AddHours(-3)); // 次老 (1KB)
            File.SetLastAccessTimeUtc(path3, DateTime.UtcNow.AddHours(-2)); // 较老 (600KB)
            File.SetLastAccessTimeUtc(path4, DateTime.UtcNow); // 最新 (600KB)

            // 总量约 1.2MB，配额为 1MB (安全线 921KB)
            // 依次清理 path, flacPath, path3 之后剩余 600KB <= 921KB，因此 path4 保留
            cacheService.PruneIfNeededAsync(1, CancellationToken.None).GetAwaiter().GetResult();
            Check("LRU已清理最老的文件", !File.Exists(path));
            Check("LRU已清理次老的文件", !File.Exists(flacPath));
            Check("LRU已清理超额的文件", !File.Exists(path3));
            Check("LRU保留最新的文件", File.Exists(path4));

            // 7. 全部清空缓存测试
            cacheService.ClearCacheAsync(CancellationToken.None).GetAwaiter().GetResult();
            var sizeCleared = cacheService.GetCacheSizeAsync().GetAwaiter().GetResult();
            Equal("清理后缓存数", 0, sizeCleared.FileCount);
            Equal("清理后缓存大小", 0L, sizeCleared.TotalBytes);
            Check("清理后文件不存在", !File.Exists(path4));

            // 8. PlaybackCoordinator 缓存命中优先播放测试
            var api = new DummyApiClient();
            var backend = new FakePlaybackBackend();
            backend.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            var coordinator = new Dianbo.Core.Playback.PlaybackCoordinator(api, backend, cacheService);

            var song999 = new Song { Id = 999L, Name = "Cached Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(200) };
            var song999Path = Path.Combine(tempDir, "999_320kmp3.mp3");
            File.WriteAllBytes(song999Path, mp3Bytes);

            coordinator.PlayQueueAsync([song999], 0, CancellationToken.None).GetAwaiter().GetResult();

            Equal("已缓存歌曲播放来源标记", true, coordinator.CurrentAudioSource?.IsFromCache);
            Equal("已缓存歌曲播放本地路径", song999Path, coordinator.CurrentAudioSource?.Url);
            Equal("后端加载的是本地文件", song999Path, backend.LastLoadedUrl);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}


