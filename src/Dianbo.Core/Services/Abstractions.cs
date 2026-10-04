using Dianbo.Core.Models;

namespace Dianbo.Core.Services;

public sealed class BodianApiException : Exception
{
    public BodianApiException(string path, int? httpStatus, int? businessCode, string message, bool retryable = true)
        : base(message)
    {
        Path = path;
        HttpStatus = httpStatus;
        BusinessCode = businessCode;
        Retryable = retryable;
    }

    public string Path { get; }
    public int? HttpStatus { get; }
    public int? BusinessCode { get; }
    public bool Retryable { get; }

    public bool IsPermissionDenied => HttpStatus is 401 or 403 || BusinessCode is 11052 or 403;
}

public enum QrPollState
{
    Waiting,
    Confirmed,
    Unusable
}

public sealed record QrCodeTicket(string QrCode, string ScanUrl);

public interface IAuthService
{
    Session? Current { get; }
    UserProfile? Profile { get; }
    bool IsSignedIn { get; }
    event EventHandler<UserProfile?>? ProfileChanged;
    Task<Session?> RestoreAsync(CancellationToken cancellationToken);
    Task<UserProfile?> GetProfileAsync(CancellationToken cancellationToken);
    Task<QrCodeTicket> CreateQrCodeAsync(CancellationToken cancellationToken);
    Task<QrPollState> PollQrCodeAsync(string qrCode, CancellationToken cancellationToken);
    Task<Session?> ExchangeQrCodeAsync(string qrCode, CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
}

public interface ISessionStore
{
    string Location { get; }
    Task<Session?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(Session session, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}

public interface IQrCodeRenderer
{
    QrCodeImage Render(string content, int size);
}

public sealed record QrCodeImage(int Width, int Height, byte[] Pixels)
{
    public static QrCodeImage Empty { get; } = new(0, 0, Array.Empty<byte>());
}

public interface IPlaybackBackend : IAsyncDisposable
{
    bool IsRunning { get; }
    bool IsConfigured { get; }
    string? LastError { get; }
    event EventHandler<BackendExitInfo>? Exited;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task LoadAsync(string url, long generation, long songId, CancellationToken cancellationToken);
    Task SetPausedAsync(bool paused, CancellationToken cancellationToken);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);
    Task StopMediaAsync(CancellationToken cancellationToken);
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken);
    IAsyncEnumerable<PlaybackSnapshot> ObserveAsync(long generation, long songId, CancellationToken cancellationToken);
}

public sealed record BackendEvent(string Kind, PlaybackEndReason Reason, string? Detail)
{
    public const string EndFile = "end-file";
    public const string FileLoaded = "file-loaded";
    public const string StartFile = "start-file";
}

public sealed record BackendExitInfo(int ExitCode, bool Expected);

public interface IBodianApiClient
{
    Task<IReadOnlyList<Song>> SearchAsync(string keyword, int pageIndex, int pageSize, CancellationToken cancellationToken);
    Task<bool> CheckPlayRightAsync(long songId, CancellationToken cancellationToken);
    Task<AudioSource> ResolveAudioSourceAsync(long songId, string bitrate, CancellationToken cancellationToken);
    Task<string?> GetLyricsAsync(long songId, CancellationToken cancellationToken);
    Task<Playlist?> GetFondPlaylistAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Playlist>> GetUserPlaylistsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(long playlistId, int pageIndex, int pageSize, CancellationToken cancellationToken, int sourceType = 5);
    Task<IReadOnlyList<Song>> GetAllPlaylistSongsAsync(long playlistId, CancellationToken cancellationToken, int sourceType = 5);
    Task<IReadOnlyList<Song>> GetRecommendSongsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<Playlist>> GetRecommendPlaylistsAsync(int count, CancellationToken cancellationToken);
    Task<bool> RecordBehaviorAsync(long songId, string operation, CancellationToken cancellationToken = default);
    Task<bool> AddPlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default);
    Task<bool> DeletePlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default);
    Task<Playlist?> CreatePlaylistAsync(string name, CancellationToken cancellationToken = default);
    Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken = default);
}

public interface IUserPlaylistService
{
    event EventHandler? PlaylistsChanged;
    Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken = default);
    Task<Playlist> CreatePlaylistAsync(string name, CancellationToken cancellationToken = default);
    Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken = default);
    Task<bool> AddSongToPlaylistAsync(long playlistId, Song song, CancellationToken cancellationToken = default);
    Task<bool> RemoveSongFromPlaylistAsync(long playlistId, long songId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(long playlistId, int sourceType = 5, CancellationToken cancellationToken = default);
    Task SyncAsync(CancellationToken cancellationToken = default);
}

public interface IHistoryService
{
    event EventHandler? HistoryChanged;
    Task<IReadOnlyList<Song>> GetRecentSongsAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task AddRecentSongAsync(Song song, CancellationToken cancellationToken = default);
    Task ClearRecentSongsAsync(CancellationToken cancellationToken = default);
}

public interface IFavoriteService
{
    event EventHandler? FavoritesChanged;
    bool IsFavorite(long songId);
    Task<IReadOnlyList<Song>> GetFavoriteSongsAsync(CancellationToken cancellationToken = default);
    Task<bool> AddFavoriteAsync(Song song, CancellationToken cancellationToken = default);
    Task<bool> RemoveFavoriteAsync(long songId, CancellationToken cancellationToken = default);
    Task<bool> ToggleFavoriteAsync(Song song, CancellationToken cancellationToken = default);
    Task SyncAsync(CancellationToken cancellationToken = default);
}

public sealed record CacheSizeInfo(long TotalBytes, int FileCount)
{
    public string FormattedSize => TotalBytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{TotalBytes} B",
        < 1024 * 1024 => $"{TotalBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{TotalBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{TotalBytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };

    public string SummaryText => FileCount == 0 ? "暂无缓存" : $"{FormattedSize}（共 {FileCount} 首）";
}

public interface IAudioCacheService
{
    string CacheDirectory { get; }

    void SetCacheDirectory(string newDirectory);

    string? GetCachedFilePath(long songId, string bitrate);

    string? FindAnyCachedFilePath(long songId);

    Task<string?> CacheAudioAsync(long songId, string bitrate, string url, long? expectedSize, CancellationToken cancellationToken = default);

    Task<CacheSizeInfo> GetCacheSizeAsync(CancellationToken cancellationToken = default);

    Task ClearCacheAsync(CancellationToken cancellationToken = default);

    Task PruneIfNeededAsync(int maxLimitMb, CancellationToken cancellationToken = default);
}
