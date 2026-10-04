using Dianbo.Core.Lyrics;

namespace Dianbo.Core.Playback;

public sealed class LyricsChangedEventArgs : EventArgs
{
    public LyricsChangedEventArgs(LyricDocument document, long? songId, long generation)
    {
        Document = document;
        SongId = songId;
        Generation = generation;
    }

    public LyricDocument Document { get; }
    public long? SongId { get; }
    public long Generation { get; }
}
