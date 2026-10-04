using System.Text.Json;
using System.Text.Json.Serialization;
using Dianbo.Core.Models;
using Dianbo.Core.Services;
using Dianbo.Infrastructure.Api;

namespace Dianbo.Infrastructure.Storage;

public sealed class LocalPlaylistRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string? CreatorName { get; set; }
    public string? CreateTime { get; set; }
    public List<Song> Songs { get; set; } = [];
}

public sealed class UserPlaylistStoreData
{
    public List<long> DeletedPlaylistIds { get; set; } = [];
    public List<LocalPlaylistRecord> LocalPlaylists { get; set; } = [];
}

internal sealed class AccountUserPlaylistStore : IUserPlaylistService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _playlistsPath;
    private readonly IBodianApiClient _api;
    private readonly IAuthCredentialSource _credentials;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<long> _deletedCloudIds = [];
    private readonly Dictionary<long, LocalPlaylistRecord> _localPlaylists = [];
    private bool _loaded;

    public AccountUserPlaylistStore(
        string playlistsPath,
        IBodianApiClient api,
        IAuthCredentialSource credentials,
        Action<string>? log = null)
    {
        _playlistsPath = playlistsPath;
        _api = api;
        _credentials = credentials;
        _log = log;
    }

    public event EventHandler? PlaylistsChanged;

    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<Playlist>();
        var seenIds = new HashSet<long>();

        if (_credentials.Session is not null)
        {
            try
            {
                var cloudPlaylists = await _api.GetUserPlaylistsAsync(0, 100, cancellationToken).ConfigureAwait(false);
                foreach (var pl in cloudPlaylists)
                {
                    if (seenIds.Add(pl.Id))
                    {
                        result.Add(pl);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"fetch cloud playlists failed: {ex.Message}");
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var local in _localPlaylists.Values)
            {
                if (seenIds.Add(local.Id))
                {
                    result.Add(ToPlaylist(local));
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return result;
    }

    public async Task<Playlist> CreatePlaylistAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("歌单名称不能为空", nameof(name));

        var trimmedName = name.Trim();
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        if (_credentials.Session is not null)
        {
            try
            {
                var cloudPl = await _api.CreatePlaylistAsync(trimmedName, cancellationToken).ConfigureAwait(false);
                if (cloudPl is not null && cloudPl.Id > 0)
                {
                    _log?.Invoke($"cloud playlist created: id={cloudPl.Id}, name={trimmedName}");
                    PlaylistsChanged?.Invoke(this, EventArgs.Empty);
                    return cloudPl;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"cloud create playlist error: {ex.Message}");
            }
        }

        var userName = _credentials.Session?.UserName ?? "我";
        var newId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var record = new LocalPlaylistRecord
        {
            Id = newId,
            Name = trimmedName,
            CreatorName = userName,
            CreateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Songs = []
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _localPlaylists[newId] = record;
            await SaveLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _log?.Invoke($"local playlist created: id={newId}, name={trimmedName}");
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return ToPlaylist(record);
    }

    public async Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        if (playlistId <= 0) return false;

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        bool local;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            local = _localPlaylists.Remove(playlistId, out var removed);
            if (local)
            {
                try { await SaveLockedAsync(cancellationToken).ConfigureAwait(false); }
                catch { _localPlaylists[playlistId] = removed!; throw; }
            }
        }
        finally { _gate.Release(); }

        if (!local)
        {
            if (_credentials.Session is null) return false;
            if (!await _api.DeletePlaylistAsync(playlistId, cancellationToken).ConfigureAwait(false))
                return false;
        }
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> AddSongToPlaylistAsync(long playlistId, Song song, CancellationToken cancellationToken = default)
    {
        if (playlistId <= 0 || song is null || song.Id <= 0) return false;

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_localPlaylists.TryGetValue(playlistId, out var local))
            {
                if (!local.Songs.Any(s => s.Id == song.Id))
                {
                    local.Songs.Add(song);
                    if (string.IsNullOrWhiteSpace(local.CoverUrl) && !string.IsNullOrWhiteSpace(song.CoverUrl))
                    {
                        local.CoverUrl = song.CoverUrl;
                    }
                    await SaveLockedAsync(cancellationToken).ConfigureAwait(false);
                }
                PlaylistsChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }
        finally
        {
            _gate.Release();
        }

        return await _api.AddPlaylistMusicAsync(playlistId, song.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveSongFromPlaylistAsync(long playlistId, long songId, CancellationToken cancellationToken = default)
    {
        if (playlistId <= 0 || songId <= 0) return false;

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_localPlaylists.TryGetValue(playlistId, out var local))
            {
                var removed = local.Songs.RemoveAll(s => s.Id == songId) > 0;
                if (removed)
                {
                    await SaveLockedAsync(cancellationToken).ConfigureAwait(false);
                    PlaylistsChanged?.Invoke(this, EventArgs.Empty);
                }
                return removed;
            }
        }
        finally
        {
            _gate.Release();
        }

        return await _api.DeletePlaylistMusicAsync(playlistId, songId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(long playlistId, int sourceType = 5, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_localPlaylists.TryGetValue(playlistId, out var local))
            {
                return local.Songs.ToList();
            }
        }
        finally
        {
            _gate.Release();
        }

        return await _api.GetAllPlaylistSongsAsync(playlistId, cancellationToken, sourceType: sourceType).ConfigureAwait(false);
    }

    public Task SyncAsync(CancellationToken cancellationToken = default)
    {
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private static Playlist ToPlaylist(LocalPlaylistRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        CoverUrl = record.CoverUrl,
        CreatorName = record.CreatorName,
        CreateTime = record.CreateTime,
        MusicCount = record.Songs.Count,
        SourceType = 5,
        IsFond = false
    };

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            _loaded = true;

            if (!File.Exists(_playlistsPath)) return;

            var json = await File.ReadAllTextAsync(_playlistsPath, cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<UserPlaylistStoreData>(json, JsonOptions);
            if (data is null) return;

            lock (_deletedCloudIds)
            {
                _deletedCloudIds.Clear();
                if (data.DeletedPlaylistIds is not null)
                {
                }
            }

            _localPlaylists.Clear();
            if (data.LocalPlaylists is not null)
            {
                foreach (var item in data.LocalPlaylists)
                {
                    _localPlaylists[item.Id] = item;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"load playlists store failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveLockedAsync(CancellationToken cancellationToken)
    {
        var data = new UserPlaylistStoreData();
        lock (_deletedCloudIds)
        {
            data.DeletedPlaylistIds = _deletedCloudIds.ToList();
        }
        data.LocalPlaylists = _localPlaylists.Values.ToList();

        var directory = Path.GetDirectoryName(_playlistsPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tempPath = _playlistsPath + ".tmp";
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _playlistsPath, overwrite: true);
    }
}
