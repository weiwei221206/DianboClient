namespace Dianbo.Core.Models;

public enum PlaybackState
{
    Idle,
    Resolving,
    Loading,
    Playing,
    Paused,
    Seeking,
    Ended,
    Failed,
    Stopping
}

public enum PlaybackEndReason
{
    None,
    EndOfFile,
    Stopped,
    Error,
    Unknown
}

public sealed record PlaybackSnapshot
{
    public static PlaybackSnapshot Initial { get; } = new();

    public long Generation { get; init; }
    public long? SongId { get; init; }
    public PlaybackState State { get; init; } = PlaybackState.Idle;
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsPaused { get; init; }
    public bool IsSeeking { get; init; }
    public PlaybackEndReason EndReason { get; init; }
    public string? Error { get; init; }
    public int Volume { get; init; } = 60;
    public AudioSource? AudioSource { get; init; }

    public bool HasMedia => SongId is not null && State is not (PlaybackState.Idle or PlaybackState.Stopping);
}
