using System.Text.Json;
using System.Text.Json.Serialization;
using Dianbo.Core.Models;
using Dianbo.Core.Services;
using Dianbo.Infrastructure.Api;

namespace Dianbo.Infrastructure.Storage;

internal sealed class AccountFavoriteStore : IFavoriteService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _favoritesPath;
    private readonly IBodianApiClient _api;
    private readonly IAuthCredentialSource _credentials;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<long> _favoriteIds = [];
    private readonly List<Song> _cachedSongs = [];
    private readonly object _pendingLock = new();
    private readonly Dictionary<long, Song> _pendingAdditions = [];
    private readonly HashSet<long> _pendingRemovals = [];
    private sealed record FavoriteState(List<Song> Songs, List<Song> PendingAdditions, List<long> PendingRemovals);
    private long _fondPlaylistId;
    private volatile bool _loaded;
    private readonly CancellationToken _accountToken;

    public AccountFavoriteStore(
        string favoritesPath,
        IBodianApiClient api,
        IAuthCredentialSource credentials,
        Action<string>? log = null, CancellationToken accountToken = default)
    {
        _favoritesPath = favoritesPath;
        _api = api;
        _credentials = credentials;
        _log = log;
        _accountToken = accountToken;
    }

    private readonly SemaphoreSlim _cloudGate = new(1, 1);
    private async Task SyncCloudAsync(Func<Task> operation)
    {
        await _cloudGate.WaitAsync(_accountToken).ConfigureAwait(false);
        try { _accountToken.ThrowIfCancellationRequested(); await operation().ConfigureAwait(false); }
        finally { _cloudGate.Release(); }
    }
    private async Task AcknowledgeAdditionAsync(Song song, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_pendingLock)
            {
                if (!_pendingAdditions.TryGetValue(song.Id, out var pending) || !ReferenceEquals(pending, song)) return;
                _pendingAdditions.Remove(song.Id);
            }
            try { await SaveToFileAsync(_cachedSongs, token).ConfigureAwait(false); }
            catch { lock (_pendingLock) _pendingAdditions[song.Id] = song; throw; }
        }
        finally { _gate.Release(); }
    }

    private async Task AcknowledgeRemovalAsync(long songId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_pendingLock) if (!_pendingRemovals.Remove(songId)) return;
            try { await SaveToFileAsync(_cachedSongs, token).ConfigureAwait(false); }
            catch { lock (_pendingLock) _pendingRemovals.Add(songId); throw; }
        }
        finally { _gate.Release(); }
    }
    public event EventHandler? FavoritesChanged;

    public bool IsFavorite(long songId)
    {
        EnsureLoadedQuick();
        lock (_favoriteIds)
        {
            return _favoriteIds.Contains(songId);
        }
    }

    public async Task<IReadOnlyList<Song>> GetFavoriteSongsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _cachedSongs.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> AddFavoriteAsync(Song song, CancellationToken cancellationToken = default)
    {
        if (song is null || song.Id <= 0) return false;

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_favoriteIds)
            {
                _favoriteIds.Add(song.Id);
            }
            lock (_pendingLock)
            {
                _pendingAdditions[song.Id] = song;
                _pendingRemovals.Remove(song.Id);
            }
            _cachedSongs.RemoveAll(s => s.Id == song.Id);
            _cachedSongs.Insert(0, song);

            await SaveToFileAsync(_cachedSongs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        FavoritesChanged?.Invoke(this, EventArgs.Empty);

        await SyncCloudAsync(async () =>
        {
            try
            {
                var pid = await EnsureFondPlaylistIdAsync(_accountToken).ConfigureAwait(false);
                if (pid > 0)
                {
                    var ok = await _api.AddPlaylistMusicAsync(pid, song.Id, _accountToken).ConfigureAwait(false);
                    if (ok) await AcknowledgeAdditionAsync(song, _accountToken).ConfigureAwait(false);
                    _log?.Invoke($"favorite add to playlist {pid} songId={song.Id} name='{song.Name}' result={ok}");
                }
                _accountToken.ThrowIfCancellationRequested();
                var recOk = await _api.RecordBehaviorAsync(song.Id, "OP_COLLECT", _accountToken).ConfigureAwait(false);
                _log?.Invoke($"favorite add record behavior songId={song.Id} result={recOk}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.Invoke($"favorite add songId={song.Id} cloudError={ex.Message}");
            }
        });

        return true;
    }

    public async Task<bool> RemoveFavoriteAsync(long songId, CancellationToken cancellationToken = default)
    {
        if (songId <= 0) return false;

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_favoriteIds)
            {
                _favoriteIds.Remove(songId);
            }
            lock (_pendingLock)
            {
                _pendingRemovals.Add(songId);
                _pendingAdditions.Remove(songId);
            }
            _cachedSongs.RemoveAll(s => s.Id == songId);

            await SaveToFileAsync(_cachedSongs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        FavoritesChanged?.Invoke(this, EventArgs.Empty);

        await SyncCloudAsync(async () =>
        {
            try
            {
                var pid = await EnsureFondPlaylistIdAsync(_accountToken).ConfigureAwait(false);
                if (pid > 0)
                {
                    var ok = await _api.DeletePlaylistMusicAsync(pid, songId, _accountToken).ConfigureAwait(false);
                    if (ok) await AcknowledgeRemovalAsync(songId, _accountToken).ConfigureAwait(false);
                    _log?.Invoke($"favorite remove from playlist {pid} songId={songId} result={ok}");
                }
                _accountToken.ThrowIfCancellationRequested();
                var recOk = await _api.RecordBehaviorAsync(songId, "OP_UNDO_COLLECT", _accountToken).ConfigureAwait(false);
                _log?.Invoke($"favorite remove record behavior songId={songId} result={recOk}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log?.Invoke($"favorite remove songId={songId} cloudError={ex.Message}");
            }
        });

        return true;
    }

    public async Task<bool> ToggleFavoriteAsync(Song song, CancellationToken cancellationToken = default)
    {
        if (song is null || song.Id <= 0) return false;

        if (IsFavorite(song.Id))
        {
            await RemoveFavoriteAsync(song.Id, cancellationToken).ConfigureAwait(false);
            return false;
        }
        else
        {
            await AddFavoriteAsync(song, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _accountToken);
        cancellationToken = linked.Token;
        await _cloudGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fond = await _api.GetFondPlaylistAsync(cancellationToken).ConfigureAwait(false);
            if (fond is null) return;
            _fondPlaylistId = fond.Id;

            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            List<Song> additions;
            List<long> removals;
            lock (_pendingLock)
            {
                additions = _pendingAdditions.Values.ToList();
                removals = _pendingRemovals.ToList();
            }
            foreach (var song in additions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await _api.AddPlaylistMusicAsync(fond.Id, song.Id, cancellationToken).ConfigureAwait(false))
                        await AcknowledgeAdditionAsync(song, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log?.Invoke($"favorite retry add songId={song.Id} cloudError={ex.Message}"); }
            }
            foreach (var songId in removals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await _api.DeletePlaylistMusicAsync(fond.Id, songId, cancellationToken).ConfigureAwait(false))
                        await AcknowledgeRemovalAsync(songId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log?.Invoke($"favorite retry remove songId={songId} cloudError={ex.Message}"); }
            }

            var serverSongs = await _api.GetAllPlaylistSongsAsync(fond.Id, cancellationToken, sourceType: 5).ConfigureAwait(false);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var merged = new List<Song>();
                var seenIds = new HashSet<long>();

                lock (_pendingLock)
                {
                    foreach (var s in serverSongs)
                    {
                        if (s.Id <= 0) continue;
                        if (_pendingRemovals.Contains(s.Id)) continue;
                        if (seenIds.Add(s.Id))
                        {
                            merged.Add(s);
                        }
                        _pendingAdditions.Remove(s.Id);
                    }

                    foreach (var (id, song) in _pendingAdditions)
                    {
                        if (seenIds.Add(id))
                        {
                            merged.Insert(0, song);
                        }
                    }


                    var serverIdSet = serverSongs.Select(s => s.Id).ToHashSet();
                    _pendingRemovals.RemoveWhere(id => !serverIdSet.Contains(id));
                }

                _cachedSongs.Clear();
                _cachedSongs.AddRange(merged);

                lock (_favoriteIds)
                {
                    _favoriteIds.Clear();
                    foreach (var id in seenIds)
                    {
                        _favoriteIds.Add(id);
                    }
                }

                await SaveToFileAsync(_cachedSongs, cancellationToken).ConfigureAwait(false);
                _log?.Invoke($"favorite synced: total={_cachedSongs.Count}, server={serverSongs.Count}");
            }
            finally
            {
                _gate.Release();
            }

            FavoritesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log?.Invoke($"favorite sync error: {ex.Message}");
        }
        finally { _cloudGate.Release(); }
    }

    private async Task<long> EnsureFondPlaylistIdAsync(CancellationToken cancellationToken)
    {
        if (_fondPlaylistId > 0) return _fondPlaylistId;
        try
        {
            var fond = await _api.GetFondPlaylistAsync(cancellationToken).ConfigureAwait(false);
            if (fond is not null && fond.Id > 0)
            {
                _fondPlaylistId = fond.Id;
                return _fondPlaylistId;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"fetch fond playlist id error: {ex.Message}");
        }
        return 0;
    }

    private void EnsureLoadedQuick()
    {
        if (_loaded) return;
        _gate.Wait();
        try
        {
            if (_loaded) return;
            LoadFromFileSync();
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            LoadFromFileSync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LoadFromFileSync()
    {
        try
        {
            if (File.Exists(_favoritesPath))
            {
                var json = File.ReadAllText(_favoritesPath);
                List<Song>? items;
                if (json.TrimStart().StartsWith('['))
                {
                    items = JsonSerializer.Deserialize<List<Song>>(json, JsonOptions);
                }
                else
                {
                    var state = JsonSerializer.Deserialize<FavoriteState>(json, JsonOptions);
                    items = state?.Songs;
                    if (state is not null)
                    {
                        lock (_pendingLock)
                        {
                            _pendingAdditions.Clear();
                            foreach (var song in state.PendingAdditions) if (song.Id > 0) _pendingAdditions[song.Id] = song;
                            _pendingRemovals.Clear();
                            foreach (var id in state.PendingRemovals) if (id > 0) _pendingRemovals.Add(id);
                        }
                    }
                }
                if (items is not null)
                {
                    _cachedSongs.Clear();
                    _cachedSongs.AddRange(items);
                    lock (_favoriteIds)
                    {
                        _favoriteIds.Clear();
                        foreach (var s in items)
                        {
                            if (s.Id > 0) _favoriteIds.Add(s.Id);
                        }
                    }
                    _loaded = true;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"load favorites from file error: {ex.Message}");
        }
        _loaded = true;
    }

    private async Task SaveToFileAsync(List<Song> songs, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_favoritesPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = _favoritesPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            FavoriteState state;
            lock (_pendingLock) state = new FavoriteState(songs.ToList(), _pendingAdditions.Values.ToList(), _pendingRemovals.ToList());
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        File.Move(tempPath, _favoritesPath, overwrite: true);
    }
}
