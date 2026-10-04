namespace Dianbo.Core.Models;

public sealed record Song
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required string Artist { get; init; }
    public string Album { get; init; } = string.Empty;
    public long AlbumId { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? CoverUrl { get; init; }
    public long DurationMs => (long)(Duration?.TotalMilliseconds ?? 0);

    public string Display => string.IsNullOrWhiteSpace(Artist) ? Name : $"{Name} — {Artist}";
}

public sealed record AudioSource
{
    public required long SongId { get; init; }
    public required string Url { get; init; }
    public string? Format { get; init; }
    public int? Bitrate { get; init; }
    public long? Size { get; init; }
    public string? SizeText { get; init; }
    public TimeSpan? Duration { get; init; }

    public bool IsTrialClip(TimeSpan? songDuration)
        => Duration is { } clip && clip > TimeSpan.Zero
           && clip < TimeSpan.FromMinutes(2)
           && songDuration is { } full && full - clip > TimeSpan.FromSeconds(20);

    public string? DisplaySize
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SizeText))
            {
                if (SizeText.EndsWith("b", StringComparison.OrdinalIgnoreCase))
                    return SizeText;
                if (long.TryParse(SizeText, out var bytes) && bytes > 0)
                    return $"{bytes / (1024.0 * 1024.0):0.1}MB";
            }
            if (Size is > 0)
            {
                return $"{Size.Value / (1024.0 * 1024.0):0.1}MB";
            }
            return null;
        }
    }

    public bool IsFromCache { get; init; }

    public string DisplayQuality
    {
        get
        {
            var fmt = (Format ?? "MP3").ToUpperInvariant();
            string qualityName;
            if (fmt.Contains("FLAC") || Bitrate >= 1000)
            {
                qualityName = "无损品质 · FLAC";
            }
            else if (Bitrate >= 320)
            {
                qualityName = $"极高品质 · {fmt}";
            }
            else
            {
                qualityName = $"标准品质 · {fmt}";
            }

            return qualityName;
        }
    }
}
