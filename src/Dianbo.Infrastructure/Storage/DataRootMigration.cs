using System.Text.Json;

namespace Dianbo.Infrastructure.Storage;

public static class DataRootMigration
{
    private const string PendingName = "pending-data-root.json";
    private static readonly string[] Files = ["session.dpapi", "settings.json", "devid.txt", "history.json", "favorites.json", "playlists.json", "queue.json"];
    private static readonly string[] Directories = ["accounts", "logs", "cache", "mpv"];

    public static void Schedule(AppPaths current, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (string.Equals(target.TrimEnd(Path.DirectorySeparatorChar), current.Root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(Path.Combine(current.Root, PendingName));
            return;
        }
        var sourcePrefix = Path.GetFullPath(current.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetPrefix = target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (targetPrefix.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) || sourcePrefix.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("新旧数据目录不能互相包含，请选择独立目录。");
        Directory.CreateDirectory(target);
        if (Files.Any(f => File.Exists(Path.Combine(target, f))) || Directories.Any(d => Directory.Exists(Path.Combine(target, d))))
            throw new IOException("目标目录已有点波数据，请选择空目录，避免覆盖其他会话或收藏。");
        var pending = Path.Combine(current.Root, PendingName);
        File.WriteAllText(pending + ".tmp", JsonSerializer.Serialize(target));
        File.Move(pending + ".tmp", pending, true);
    }

    public static AppPaths ApplyPending(AppPaths current)
    {
        var pending = Path.Combine(current.Root, PendingName);
        if (!File.Exists(pending)) return current;
        try
        {
            var target = JsonSerializer.Deserialize<string>(File.ReadAllText(pending));
            if (string.IsNullOrWhiteSpace(target)) return current;
            var next = new AppPaths(target);
            Directory.CreateDirectory(next.Root);
            foreach (var file in Files)
            {
                var source = Path.Combine(current.Root, file);
                if (File.Exists(source)) CopyAtomic(source, Path.Combine(next.Root, file));
            }
            foreach (var directory in Directories)
                CopyTree(Path.Combine(current.Root, directory), Path.Combine(next.Root, directory));
            AppPaths.SaveCustomRoot(next.IsCustomRoot ? next.Root : null);
            if (!string.Equals(Path.GetFullPath(AppPaths.ResolveDefaultRoot()), Path.GetFullPath(next.Root), StringComparison.OrdinalIgnoreCase))
                throw new IOException("无法保存数据目录设置");
            try { File.Delete(pending); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return next;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            new RedactedLog(current.LogPath).Write($"data migration failed: {error.GetType().Name}; keeping original data root");
            return current;
        }
    }

    private static void CopyAtomic(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination + ".migration-tmp", true);
        File.Move(destination + ".migration-tmp", destination, true);
    }

    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source)) CopyAtomic(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
