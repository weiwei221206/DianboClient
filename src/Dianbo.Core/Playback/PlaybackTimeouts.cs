namespace Dianbo.Core.Playback;

public sealed class PlaybackTimeouts
{
    public TimeSpan Api { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan IpcCommand { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MediaOpen { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan Lyrics { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan BackendStop { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);
}
