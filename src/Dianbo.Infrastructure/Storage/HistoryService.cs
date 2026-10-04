using System.Text.Json;
using System.Text.Json.Serialization;
using Dianbo.Core.Models;
using Dianbo.Core.Services;

namespace Dianbo.Infrastructure.Storage;

public sealed class HistoryService : IHistoryService
{
    private const int MaxHistoryCount = 200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _historyPath;
    private readonly Action<string>? _log;
    private readonly bool _enableLegacyMigration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<Song>? _cachedSongs;

    public HistoryService(string historyPath, Action<string>? log = null, bool enableLegacyMigration = true)
    {
        _historyPath = historyPath;
        _log = log;
        _enableLegacyMigration = enableLegacyMigration;
    }

    public event EventHandler? HistoryChanged;

    public async Task<IReadOnlyList<Song>> GetRecentSongsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedSongs is null || _cachedSongs.Count == 0)
                return Array.Empty<Song>();

            var count = Math.Clamp(limit, 1, _cachedSongs.Count);
            return _cachedSongs.Take(count).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddRecentSongAsync(Song song, CancellationToken cancellationToken = default)
    {
        if (song is null || song.Id <= 0) return;

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cachedSongs ??= [];

            _cachedSongs.RemoveAll(s => s.Id == song.Id);
            _cachedSongs.Insert(0, song);

            if (_cachedSongs.Count > MaxHistoryCount)
            {
                _cachedSongs.RemoveRange(MaxHistoryCount, _cachedSongs.Count - MaxHistoryCount);
            }

            await SaveToFileAsync(_cachedSongs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearRecentSongsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cachedSongs = [];
            await SaveToFileAsync(_cachedSongs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cachedSongs is not null) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedSongs is not null) return;
            _cachedSongs = await LoadFromFileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<Song>> LoadFromFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_enableLegacyMigration && !File.Exists(_historyPath))
            {
                var legacyPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BodianClient",
                    "history.json");
                if (File.Exists(legacyPath))
                {
                    AppPaths.CopyIfMissing(legacyPath, _historyPath);
                }
            }

            if (!File.Exists(_historyPath))
            {
                return [];
            }

            await using var stream = File.OpenRead(_historyPath);
            var songs = await JsonSerializer.DeserializeAsync<List<Song>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            _log?.Invoke($"history load count={songs?.Count ?? 0}");
            return songs ?? [];
        }
        catch (Exception ex)
        {
            _log?.Invoke($"history load failed: {ex.Message}");
            return [];
        }
    }

    private async Task SaveToFileAsync(List<Song> songs, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tempPath = _historyPath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, songs, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _historyPath, overwrite: true);
            _log?.Invoke($"history save count={songs.Count}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"history save failed: {ex.Message}");
        }
    }
}
