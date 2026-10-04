using System.Collections.Concurrent;
using Dianbo.Core.Services;

namespace Dianbo.Infrastructure.Storage;

public sealed class AudioCacheService : IAudioCacheService
{
    private const string CacheVersionMarker = ".audio-cache-v2";
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlightDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dirGate = new();
    private string _cacheDirectory;

    public AudioCacheService(string cacheDirectory, HttpClient? http = null, Action<string>? log = null)
    {
        _cacheDirectory = cacheDirectory;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _log = log;
        EnsureDirectory();
        InvalidateLegacyCache();
    }

    public TimeSpan ReadInactivityTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public string CacheDirectory
    {
        get
        {
            lock (_dirGate) return _cacheDirectory;
        }
    }

    public void SetCacheDirectory(string newDirectory)
    {
        lock (_dirGate)
        {
            _cacheDirectory = newDirectory;
            EnsureDirectory();
            InvalidateLegacyCache();
        }
    }

    public string? GetCachedFilePath(long songId, string bitrate)
    {
        var dir = CacheDirectory;
        if (!File.Exists(Path.Combine(dir, CacheVersionMarker))) return null;

        var ext = ResolveExtension(bitrate);
        var directPath = Path.Combine(dir, $"{songId}_{bitrate}.{ext}");
        if (IsValidCacheFile(directPath))
        {
            TouchAccessTime(directPath);
            return directPath;
        }

        try
        {
            var pattern = $"{songId}_{bitrate}.*";
            foreach (var file in Directory.GetFiles(dir, pattern))
            {
                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsValidCacheFile(file))
                {
                    TouchAccessTime(file);
                    return file;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"audio cache lookup error: {ex.Message}");
        }

        return null;
    }

    public string? FindAnyCachedFilePath(long songId)
    {
        var dir = CacheDirectory;
        if (!File.Exists(Path.Combine(dir, CacheVersionMarker))) return null;

        try
        {
            var pattern = $"{songId}_*.*";
            var files = Directory.GetFiles(dir, pattern)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Where(IsValidCacheFile)
                .ToList();

            if (files.Count == 0) return null;

            var best = files
                .OrderByDescending(f => f.Contains("flac", StringComparison.OrdinalIgnoreCase) || f.Contains("2000", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => f.Contains("320", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => f.Contains("128", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (best != null)
            {
                TouchAccessTime(best);
                return best;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"audio cache any-lookup error: {ex.Message}");
        }

        return null;
    }

    public async Task<string?> CacheAudioAsync(long songId, string bitrate, string url, long? expectedSize, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (File.Exists(url)) return url;

        var existing = GetCachedFilePath(songId, bitrate);
        if (existing != null) return existing;

        var key = $"{songId}_{bitrate}";
        var download = _inFlightDownloads.GetOrAdd(key, _ => new Lazy<Task<string?>>(
            () => DownloadCoreAsync(songId, bitrate, url, expectedSize, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await download.Value.ConfigureAwait(false);
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<string?>>>>)_inFlightDownloads)
                .Remove(new(key, download));
        }
    }

    public Task<CacheSizeInfo> GetCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var dir = CacheDirectory;
            if (!Directory.Exists(dir)) return new CacheSizeInfo(0, 0);

            try
            {
                var files = new DirectoryInfo(dir).GetFiles()
                    .Where(f => f.Name != CacheVersionMarker && !f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var totalBytes = files.Sum(f => f.Length);
                return new CacheSizeInfo(totalBytes, files.Count);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"audio cache size query error: {ex.Message}");
                return new CacheSizeInfo(0, 0);
            }
        }, cancellationToken);
    }

    public Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var dir = CacheDirectory;
            if (!Directory.Exists(dir)) return;

            try
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    if (Path.GetFileName(file) == CacheVersionMarker) continue;
                    try { File.Delete(file); }
                    catch {  }
                }
                _log?.Invoke("audio cache cleared");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"audio cache clear error: {ex.Message}");
            }
        }, cancellationToken);
    }

    public Task PruneIfNeededAsync(int maxLimitMb, CancellationToken cancellationToken = default)
    {
        if (maxLimitMb <= 0) return Task.CompletedTask;

        return Task.Run(() =>
        {
            var dir = CacheDirectory;
            if (!Directory.Exists(dir)) return;

            try
            {
                var dirInfo = new DirectoryInfo(dir);
                var allFiles = dirInfo.GetFiles().ToList();

                var staleThreshold = DateTime.UtcNow.AddHours(-1);
                foreach (var tmp in allFiles.Where(f => f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)))
                {
                    if (tmp.LastWriteTimeUtc < staleThreshold)
                    {
                        try { tmp.Delete(); } catch { }
                    }
                }

                var audioFiles = allFiles
                    .Where(f => f.Name != CacheVersionMarker && !f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var totalBytes = audioFiles.Sum(f => f.Length);
                var maxBytes = maxLimitMb * 1024L * 1024L;

                if (totalBytes <= maxBytes) return;

                var targetBytes = (long)(maxBytes * 0.9);
                var sorted = audioFiles.OrderBy(f => f.LastAccessTimeUtc).ToList();
                var removedCount = 0;

                foreach (var file in sorted)
                {
                    if (totalBytes <= targetBytes) break;
                    var len = file.Length;
                    try
                    {
                        file.Delete();
                        totalBytes -= len;
                        removedCount++;
                    }
                    catch
                    {
                    }
                }

                if (removedCount > 0)
                {
                    _log?.Invoke($"audio cache pruned: removed {removedCount} files, remaining={totalBytes / (1024 * 1024)}MB");
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"audio cache prune error: {ex.Message}");
            }
        }, cancellationToken);
    }

    private async Task<string?> DownloadCoreAsync(long songId, string bitrate, string url, long? expectedSize, CancellationToken cancellationToken)
    {
        var dir = CacheDirectory;
        EnsureDirectory();

        var ext = ResolveExtension(bitrate);
        var targetFile = Path.Combine(dir, $"{songId}_{bitrate}.{ext}");
        var tmpFile = Path.Combine(dir, $"{songId}_{bitrate}_{Guid.NewGuid():N}.tmp");

        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadCts.CancelAfter(TimeSpan.FromSeconds(180));

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log?.Invoke($"audio cache download failed: song={songId} http={response.StatusCode}");
                return null;
            }

            await using (var contentStream = await response.Content.ReadAsStreamAsync(downloadCts.Token).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(downloadCts.Token);
                    readCts.CancelAfter(ReadInactivityTimeout);

                    int bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token).ConfigureAwait(false);
                    if (bytesRead == 0) break;

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), downloadCts.Token).ConfigureAwait(false);
                }
            }

            var fi = new FileInfo(tmpFile);
            if (!fi.Exists || fi.Length < 1024)
            {
                _log?.Invoke($"audio cache file invalid/truncated: song={songId} length={fi.Length}");
                TryDelete(tmpFile);
                return null;
            }

            if (!VerifyAudioHeader(tmpFile, ext))
            {
                _log?.Invoke($"audio cache header mismatch: song={songId} ext={ext}");
                TryDelete(tmpFile);
                return null;
            }

            if (File.Exists(targetFile))
            {
                try { File.Delete(targetFile); } catch { }
            }

            File.Move(tmpFile, targetFile, overwrite: true);
            TouchAccessTime(targetFile);

            _log?.Invoke($"audio cached: song={songId} br={bitrate} bytes={fi.Length} -> {Path.GetFileName(targetFile)}");
            return targetFile;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmpFile);
            return null;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"audio cache download exception: song={songId} err={ex.Message}");
            TryDelete(tmpFile);
            return null;
        }
    }

    private static bool VerifyAudioHeader(string filePath, string ext)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[16];
            var read = fs.Read(header, 0, header.Length);
            if (read < 4) return false;

            if (ext.Equals("flac", StringComparison.OrdinalIgnoreCase))
            {
                return header[0] == 0x66 && header[1] == 0x4C && header[2] == 0x61 && header[3] == 0x43;
            }

            if (ext.Equals("mp3", StringComparison.OrdinalIgnoreCase))
            {
                var isId3 = header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33;
                var isSync = header[0] == 0xFF && (header[1] & 0xE0) == 0xE0;
                return isId3 || isSync;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidCacheFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists && fi.Length >= 128;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveExtension(string bitrate)
    {
        var lower = bitrate.ToLowerInvariant();
        if (lower.Contains("flac")) return "flac";
        if (lower.Contains("aac") || lower.Contains("m4a")) return "m4a";
        return "mp3";
    }

    private static void TouchAccessTime(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private void EnsureDirectory()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_cacheDirectory) && !Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }
        catch
        {
        }
    }

    private void InvalidateLegacyCache()
    {
        try
        {
            var marker = Path.Combine(_cacheDirectory, CacheVersionMarker);
            if (File.Exists(marker)) return;
            foreach (var file in Directory.GetFiles(_cacheDirectory))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var extension = Path.GetExtension(file).ToLowerInvariant();
                var separator = name.IndexOf('_');
                if (separator <= 0 || !long.TryParse(name[..separator], out _)) continue;
                if (extension is not (".mp3" or ".flac" or ".m4a" or ".aac")) continue;
                File.Delete(file);
            }
            File.WriteAllText(marker, "Audio cache v2: trial clips are not cached.");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"audio cache migration error: {ex.Message}");
        }
    }
}
