using System.Diagnostics;

namespace Dianbo.Infrastructure.Playback;

public enum MpvSourceKind
{
    UserSetting,
    EnvironmentVariable,
    AppDirectory,
    LocalAppData,
    DevValidation,
    Path
}

public sealed record MpvCandidate(string Path, MpvSourceKind Source)
{
    public string SourceLabel => MpvLocator.Label(Source);
}

public sealed record MpvProbeResult(bool IsMpv, string? Version, string? Banner, string? Error)
{
    public static MpvProbeResult NotFound { get; } = new(false, null, null, "未找到后端可执行文件");

    public string Summary => IsMpv
        ? (Version is null ? "已识别为 mpv" : $"mpv {Version}")
        : (Error ?? "无法识别该文件");
}

public static class MpvLocator
{
    public const string EnvironmentVariableName = "DIANBO_MPV_PATH";
    public const string ExecutableName = "mpv.exe";
    public const string DevValidationRelativePath = @"validation\.deps\mpv-ci\mpv.exe";

    private const int MaxParentLevels = 8;

    private static readonly string[] RepositoryMarkers =
    {
        "DianboClient.sln",
        "程序开发详细说明.md",
        "客户端接口可行性验证计划.md"
    };

    public static string Label(MpvSourceKind kind) => kind switch
    {
        MpvSourceKind.UserSetting => "用户指定",
        MpvSourceKind.EnvironmentVariable => $"环境变量 {EnvironmentVariableName}",
        MpvSourceKind.AppDirectory => "应用目录",
        MpvSourceKind.LocalAppData => "本机后端目录",
        MpvSourceKind.DevValidation => "开发验证目录",
        MpvSourceKind.Path => "系统 PATH",
        _ => "未知来源"
    };

    public static IReadOnlyList<MpvCandidate> FindAll(
        string? configuredPath = null,
        string? appDirectory = null,
        string? localAppDataRoot = null,
        string? environmentValue = null,
        string? searchPath = null,
        string? currentDirectory = null)
    {
        appDirectory ??= AppContext.BaseDirectory;
        localAppDataRoot ??= Storage.AppPaths.ResolveDefaultRoot();
        environmentValue ??= Environment.GetEnvironmentVariable(EnvironmentVariableName);
        searchPath ??= Environment.GetEnvironmentVariable("PATH");
        currentDirectory ??= Environment.CurrentDirectory;

        var candidates = new List<MpvCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, MpvSourceKind source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch (Exception) { return; }
            if (!File.Exists(full)) return;
            if (!seen.Add(full)) return;
            candidates.Add(new MpvCandidate(full, source));
        }

        Add(configuredPath, MpvSourceKind.UserSetting);
        Add(environmentValue, MpvSourceKind.EnvironmentVariable);
        Add(System.IO.Path.Combine(appDirectory, "mpv", ExecutableName), MpvSourceKind.AppDirectory);
        Add(System.IO.Path.Combine(appDirectory, ExecutableName), MpvSourceKind.AppDirectory);
        if (!string.IsNullOrWhiteSpace(localAppDataRoot))
        {
            Add(System.IO.Path.Combine(localAppDataRoot, "mpv", ExecutableName), MpvSourceKind.LocalAppData);
            Add(System.IO.Path.Combine(localAppDataRoot, ExecutableName), MpvSourceKind.LocalAppData);
        }
        Add(FindDevValidation(appDirectory) ?? FindDevValidation(currentDirectory), MpvSourceKind.DevValidation);
        Add(FindOnPath(searchPath), MpvSourceKind.Path);
        return candidates;
    }

    public static string? FindDevValidation(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory)) return null;
        try
        {
            var directory = new DirectoryInfo(System.IO.Path.GetFullPath(startDirectory));
            for (var level = 0; level <= MaxParentLevels && directory is not null; level++)
            {
                var candidate = System.IO.Path.Combine(directory.FullName, DevValidationRelativePath);
                if (File.Exists(candidate) && HasRepositoryMarker(directory)) return candidate;

                var nested = System.IO.Path.Combine(directory.FullName, ".deps", "mpv-ci", ExecutableName);
                if (directory.Parent is { } parent && File.Exists(nested) && HasRepositoryMarker(parent)) return nested;

                directory = directory.Parent;
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static bool HasRepositoryMarker(DirectoryInfo repositoryRoot)
    {
        try
        {
            foreach (var marker in RepositoryMarkers)
            {
                if (File.Exists(System.IO.Path.Combine(repositoryRoot.FullName, marker))) return true;
            }
        }
        catch (Exception)
        {
        }
        return false;
    }

    private static string? FindOnPath(string? searchPath)
    {
        if (string.IsNullOrWhiteSpace(searchPath)) return null;
        foreach (var entry in searchPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (directory.Length == 0) continue;
            try
            {
                if (directory.Contains('%')) directory = Environment.ExpandEnvironmentVariables(directory);
                var candidate = System.IO.Path.Combine(directory, ExecutableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    public static async Task<MpvProbeResult> ProbeAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return MpvProbeResult.NotFound;

        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--version");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new MpvProbeResult(false, null, null, "无法启动该文件");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return new MpvProbeResult(false, null, null, "版本探测超时");
            }

            var text = await standardOutput.ConfigureAwait(false);
            var banner = FirstLine(text) ?? FirstLine(await standardError.ConfigureAwait(false));
            var version = ParseVersion(text);
            return version is null
                ? new MpvProbeResult(false, null, banner, "该文件不是 mpv（无法识别 --version 输出）")
                : new MpvProbeResult(true, version, banner, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MpvProbeResult(false, null, null, exception.Message);
        }
    }

    public static string? ParseVersion(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput)) return null;
        foreach (var rawLine in versionOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("mpv ", StringComparison.OrdinalIgnoreCase)) continue;
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            var version = tokens[1].Trim();
            if (version.Length is 0 or > 64) continue;
            if (!version.Any(char.IsDigit)) continue;
            return version;
        }
        return null;
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length > 0) return line.Length > 160 ? line[..160] : line;
        }
        return null;
    }
}
