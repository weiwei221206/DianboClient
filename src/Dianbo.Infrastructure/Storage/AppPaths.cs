using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dianbo.Infrastructure.Storage;

public sealed class AppSettings
{
    public int Volume { get; set; } = 60;
    public string PlayMode { get; set; } = "RepeatAll";
    public string PreferredQuality { get; set; } = "2000kflac";
    public bool ShowTranslation { get; set; } = true;
    public bool AutoFollowLyrics { get; set; } = true;
    public int LyricFollowDelaySeconds { get; set; } = 4;
    public bool ClickLyricToSeek { get; set; } = true;
    public string? MpvPath { get; set; }
    public bool AutoSkipUnplayable { get; set; }
    public bool RetryOnceOnFailure { get; set; } = true;
    public bool UseSystemMediaKeys { get; set; } = true;
    public bool AutoNavigateToNowPlayingOnPlay { get; set; } = true;
    public bool EnableAudioCache { get; set; } = true;
    public int AudioCacheLimitMb { get; set; } = 1024;
    public bool AutoStartOnBoot { get; set; }
    public bool SilentAutoStart { get; set; } = true;
    public bool AutoPlayOnLaunch { get; set; }
    public string CloseBehavior { get; set; } = "MinimizeToTray";
    public string Theme { get; set; } = "Default";
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private string _path;

    public SettingsStore(string path) => _path = path;

    public string Location
    {
        get => _path;
        set => _path = value;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class AppPaths
{
    public const string RegistryKey = @"Software\DianboClient";
    public const string RegistryValueName = "DataDirectory";

    public AppPaths(string? root = null)
    {
        Root = root ?? ResolveDefaultRoot();
        SessionPath = Path.Combine(Root, "session.dpapi");
        SettingsPath = Path.Combine(Root, "settings.json");
        DeviceIdPath = Path.Combine(Root, "devid.txt");
        LogPath = Path.Combine(Root, "logs", "dianbo.log");
        CoverCachePath = Path.Combine(Root, "cache", "covers");
        AudioCachePath = Path.Combine(Root, "cache", "audio");
        BackendDirectory = Path.Combine(Root, "mpv");
        HistoryPath = Path.Combine(Root, "history.json");
        FavoritesPath = Path.Combine(Root, "favorites.json");
        PlaylistsPath = Path.Combine(Root, "playlists.json");
        QueuePath = Path.Combine(Root, "queue.json");
    }

    public string Root { get; }
    public string SessionPath { get; }
    public string SettingsPath { get; }
    public string DeviceIdPath { get; }
    public string LogPath { get; }
    public string CoverCachePath { get; }
    public string AudioCachePath { get; }
    public string HistoryPath { get; }
    public string FavoritesPath { get; }
    public string PlaylistsPath { get; }
    public string QueuePath { get; }
    public string BackendDirectory { get; }

    public bool IsCustomRoot => !string.Equals(
        Path.GetFullPath(Root),
        Path.GetFullPath(GetStandardDefaultRoot()),
        StringComparison.OrdinalIgnoreCase);

    public static string GetStandardDefaultRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        }
        return Path.Combine(documents, "DianboClient");
    }

    public static string ResolveDefaultRoot()
    {
        var custom = LoadCustomRoot();
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }
        return GetStandardDefaultRoot();
    }

    public static string? LoadCustomRoot()
    {
        var isolated = Environment.GetEnvironmentVariable("DIANBO_CONFIG_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(isolated))
        {
            var file = Path.Combine(isolated, "custom_data_dir.txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey);
            if (key?.GetValue(RegistryValueName) is string path && !string.IsNullOrWhiteSpace(path))
            {
                return path.Trim();
            }
        }
        catch
        {
        }

        try
        {
            var fallbackFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DianboClient", "custom_data_dir.txt");
            if (File.Exists(fallbackFile))
            {
                var content = File.ReadAllText(fallbackFile).Trim();
                if (!string.IsNullOrWhiteSpace(content)) return content;
            }
        }
        catch
        {
        }

        return null;
    }

    public static void SaveCustomRoot(string? customRoot)
    {
        var isolated = Environment.GetEnvironmentVariable("DIANBO_CONFIG_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(isolated))
        {
            Directory.CreateDirectory(isolated);
            var file = Path.Combine(isolated, "custom_data_dir.txt");
            if (string.IsNullOrWhiteSpace(customRoot)) File.Delete(file);
            else { File.WriteAllText(file + ".tmp", customRoot); File.Move(file + ".tmp", file, true); }
            return;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(customRoot))
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
                key?.DeleteValue(RegistryValueName, throwOnMissingValue: false);
            }
            else
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryKey);
                key?.SetValue(RegistryValueName, customRoot);
            }
        }
        catch
        {
        }

        try
        {
            var fallbackFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DianboClient", "custom_data_dir.txt");
            if (string.IsNullOrWhiteSpace(customRoot))
            {
                if (File.Exists(fallbackFile)) File.Delete(fallbackFile);
            }
            else
            {
                var dir = Path.GetDirectoryName(fallbackFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(fallbackFile, customRoot);
            }
        }
        catch
        {
        }
    }

    public static void CopyIfMissing(string source, string destination)
    {
        try
        {
            if (File.Exists(source) && !File.Exists(destination))
            {
                var dir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(source, destination, overwrite: false);
            }
        }
        catch
        {
        }
    }

    public static void CopyDirectoryIfMissing(string sourceDir, string destinationDir)
    {
        try
        {
            if (!Directory.Exists(sourceDir)) return;
            Directory.CreateDirectory(destinationDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var target = Path.Combine(destinationDir, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target, overwrite: false);
            }
            foreach (var sub in Directory.GetDirectories(sourceDir))
            {
                CopyDirectoryIfMissing(sub, Path.Combine(destinationDir, Path.GetFileName(sub)));
            }
        }
        catch
        {
        }
    }

    public void EnsureCreated()
    {
        TryCreate(Root);
        TryCreate(Path.GetDirectoryName(LogPath));
        TryCreate(CoverCachePath);
        TryCreate(AudioCachePath);
        MigrateLegacyIfPresent();
    }

    private void MigrateLegacyIfPresent()
    {
        try
        {
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BodianClient");
            if (Directory.Exists(legacyDir) && !string.Equals(Path.GetFullPath(legacyDir), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase))
            {
                CopyIfMissing(Path.Combine(legacyDir, "session.dpapi"), SessionPath);
                CopyIfMissing(Path.Combine(legacyDir, "devid.txt"), DeviceIdPath);
                CopyIfMissing(Path.Combine(legacyDir, "settings.json"), SettingsPath);
                CopyIfMissing(Path.Combine(legacyDir, "history.json"), HistoryPath);
                CopyIfMissing(Path.Combine(legacyDir, "favorites.json"), FavoritesPath);
            }
        }
        catch
        {
        }
    }

    private static void TryCreate(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class RedactedLog : IDisposable
{
    private const long MaxBytes = 512 * 1024;
    private readonly string _path;
    private readonly object _sync = new();

    public RedactedLog(string path) => _path = path;

    public void Write(string message)
    {
        try
        {
            lock (_sync)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                Roll();
                File.AppendAllText(_path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {Sanitize(message)}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string Sanitize(string message)
    {
        message = System.Text.RegularExpressions.Regex.Replace(message,
            @"(?i)(token|qrCode|uid|fromUid|authorization)\s*[=:]\s*[^\s&,;]+", "$1=[已省略]");
        message = System.Text.RegularExpressions.Regex.Replace(message,
            @"(?i)(ucenter/users/pub/)[^?\s]+", "$1[account]");
        var index = message.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var end = index;
            while (end < message.Length && !char.IsWhiteSpace(message[end])) end++;
            message = message[..index] + "[地址已省略]" + message[end..];
            index = message.IndexOf("http", index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return message;
    }

    private void Roll()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes) return;
        var backup = _path + ".1";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(_path, backup);
    }

    public void Dispose()
    {
    }
}
