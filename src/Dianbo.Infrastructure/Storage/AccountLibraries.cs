using Dianbo.Core.Models;
using Dianbo.Core.Services;
using Dianbo.Infrastructure.Api;

namespace Dianbo.Infrastructure.Storage;

internal sealed record FixedCredentials(Session? Session, string DevId) : IAuthCredentialSource;

internal sealed class AccountLibrary<T> where T : class
{
    private readonly object _sync = new();
    private readonly IAuthCredentialSource _credentials;
    private readonly IBodianApiClient _api;
    private readonly Func<string, IBodianApiClient, IAuthCredentialSource, CancellationToken, T> _create;
    private readonly string _basePath;
    private long? _uid;
    private string? _sessionToken;
    private T? _value;
    private CancellationTokenSource _lifetime = new();

    public AccountLibrary(string path, IBodianApiClient api, IAuthCredentialSource credentials,
        Func<string, IBodianApiClient, IAuthCredentialSource, CancellationToken, T> create)
    {
        _basePath = path; _api = api; _credentials = credentials; _create = create;
        if (credentials is IAuthService auth) auth.ProfileChanged += (_, _) => RefreshAccount();
    }

    private void RefreshAccount()
    {
        lock (_sync)
        {
            if (_value is not null && (_uid != _credentials.Session?.Uid || _sessionToken != _credentials.Session?.Token))
            {
                _lifetime.Cancel();
                _value = null;
                _lifetime.Dispose();
                _lifetime = new CancellationTokenSource();
            }
        }
    }

    public (T Store, CancellationToken Token) Get()
    {
        lock (_sync)
        {
            RefreshAccount();
            if (_value is null)
            {
                var credentials = new FixedCredentials(_credentials.Session, _credentials.DevId);
                _uid = credentials.Session?.Uid;
                _sessionToken = credentials.Session?.Token;
                var path = Path.Combine(Path.GetDirectoryName(_basePath)!, "accounts",
                    _uid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "guest", Path.GetFileName(_basePath));
                var api = _api is BodianApiClient client ? client.WithCredentials(credentials) : _api;
                _value = _create(path, api, credentials, _lifetime.Token);
            }
            return (_value, _lifetime.Token);
        }
    }

    public bool IsCurrent(T store) { lock (_sync) return ReferenceEquals(_value, store) && _uid == _credentials.Session?.Uid && _sessionToken == _credentials.Session?.Token; }

    public async Task<TResult> Run<TResult>(Func<T, CancellationToken, Task<TResult>> action, CancellationToken token)
    {
        var entry = Get();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Token);
        var result = await action(entry.Store, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        if (!IsCurrent(entry.Store)) throw new OperationCanceledException("账号已经切换");
        return result;
    }
}

public sealed class FavoriteService : IFavoriteService
{
    private readonly AccountLibrary<AccountFavoriteStore> _accounts;
    public FavoriteService(string path, IBodianApiClient api, IAuthCredentialSource credentials, Action<string>? log = null)
    {
        _accounts = new(path, api, credentials, (file, client, fixedCredentials, token) =>
        {
            var store = new AccountFavoriteStore(file, client, fixedCredentials, log, token);
            store.FavoritesChanged += (_, _) => { if (_accounts!.IsCurrent(store)) FavoritesChanged?.Invoke(this, EventArgs.Empty); };
            return store;
        });
    }
    public event EventHandler? FavoritesChanged;
    public bool IsFavorite(long songId) => _accounts.Get().Store.IsFavorite(songId);
    public Task<IReadOnlyList<Song>> GetFavoriteSongsAsync(CancellationToken cancellationToken = default) => _accounts.Run((s, t) => s.GetFavoriteSongsAsync(t), cancellationToken);
    public Task<bool> AddFavoriteAsync(Song song, CancellationToken cancellationToken = default) => _accounts.Run((s, t) => s.AddFavoriteAsync(song, t), cancellationToken);
    public Task<bool> RemoveFavoriteAsync(long songId, CancellationToken cancellationToken = default) => _accounts.Run((s, t) => s.RemoveFavoriteAsync(songId, t), cancellationToken);
    public Task<bool> ToggleFavoriteAsync(Song song, CancellationToken cancellationToken = default) => _accounts.Run((s, t) => s.ToggleFavoriteAsync(song, t), cancellationToken);
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        try { await _accounts.Run(async (s, t) => { await s.SyncAsync(t).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}

public sealed class UserPlaylistService : IUserPlaylistService
{
    private readonly AccountLibrary<AccountUserPlaylistStore> _accounts;
    public UserPlaylistService(string path, IBodianApiClient api, IAuthCredentialSource credentials, Action<string>? log = null)
    {
        _accounts = new(path, api, credentials, (file, client, fixedCredentials, token) =>
        {
            var store = new AccountUserPlaylistStore(file, client, fixedCredentials, log);
            store.PlaylistsChanged += (_, _) => { if (_accounts!.IsCurrent(store)) PlaylistsChanged?.Invoke(this, EventArgs.Empty); };
            return store;
        });
    }
    public event EventHandler? PlaylistsChanged;
    public Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default) => _accounts.Run((s,t) => s.GetPlaylistsAsync(t), cancellationToken);
    public Task<Playlist> CreatePlaylistAsync(string name, CancellationToken cancellationToken = default) => _accounts.Run((s,t) => s.CreatePlaylistAsync(name,t), cancellationToken);
    public Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken = default) => _accounts.Run((s,t) => s.DeletePlaylistAsync(playlistId,t), cancellationToken);
    public Task<bool> AddSongToPlaylistAsync(long playlistId, Song song, CancellationToken cancellationToken = default) => _accounts.Run((s,t) => s.AddSongToPlaylistAsync(playlistId,song,t), cancellationToken);
    public Task<bool> RemoveSongFromPlaylistAsync(long playlistId, long songId, CancellationToken cancellationToken = default) => _accounts.Run((s,t) => s.RemoveSongFromPlaylistAsync(playlistId,songId,t), cancellationToken);
    public Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(long playlistId, int sourceType = 5, CancellationToken cancellationToken = default) => _accounts.Run((s,t) => s.GetPlaylistSongsAsync(playlistId,sourceType,t), cancellationToken);
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        try { await _accounts.Run(async (s,t) => { await s.SyncAsync(t).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
