using System.Globalization;
using System.Text;

namespace Dianbo.Core.Lyrics;

public sealed record LyricGroup
{
    public required long StartTimeMs { get; init; }
    public required IReadOnlyList<string> OriginalLines { get; init; }
    public required IReadOnlyList<string> TranslationLines { get; init; }

    public string OriginalText => string.Join(" ", OriginalLines);
    public string TranslationText => string.Join(" ", TranslationLines);
    public bool HasTranslation => TranslationLines.Count > 0;
}

public sealed class LyricDocument
{
    public static LyricDocument Empty { get; } = new(Array.Empty<LyricGroup>(), 0, Array.Empty<string>(), null);

    public LyricDocument(
        IReadOnlyList<LyricGroup> groups,
        long sourceOffsetMs,
        IReadOnlyList<string> unpairedLines,
        string? title)
    {
        Groups = groups;
        SourceOffsetMs = sourceOffsetMs;
        UnpairedLines = unpairedLines;
        Title = title;
        _starts = groups.Select(group => group.StartTimeMs).ToArray();
        HasTimeline = _starts.Length > 0;
    }

    private readonly long[] _starts;

    public IReadOnlyList<LyricGroup> Groups { get; }
    public long SourceOffsetMs { get; }
    public IReadOnlyList<string> UnpairedLines { get; }
    public string? Title { get; }
    public bool HasTimeline { get; }

    public int IndexAt(TimeSpan position)
    {
        if (_starts.Length == 0) return -1;
        var target = (long)position.TotalMilliseconds;
        var index = BinarySearchLastAtOrBefore(_starts, target);
        return index;
    }

    public LyricGroup? GroupAt(TimeSpan position)
    {
        var index = IndexAt(position);
        return index < 0 ? null : Groups[index];
    }

    internal static int BinarySearchLastAtOrBefore(long[] sorted, long value)
    {
        var low = 0;
        var high = sorted.Length - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            if (sorted[middle] <= value)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return result;
    }
}
