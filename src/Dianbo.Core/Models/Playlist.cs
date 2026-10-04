namespace Dianbo.Core.Models;

public sealed record Playlist
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public string? CoverUrl { get; init; }
    public int MusicCount { get; init; }
    public bool IsFond { get; init; }
    public string? CreatorName { get; init; }
    public long? CreatorId { get; init; }
    public string? Description { get; init; }
    public string? CreateTime { get; init; }
    public long? PlayCount { get; init; }
    public int SourceType { get; init; } = 5;

    public string DisplaySubtitle =>
        (MusicCount > 0 ? $"{MusicCount} 首歌曲" : (PlayCount is > 0 ? $"{FormatPlayCount(PlayCount.Value)}次播放" : "精选歌单"))
        + (string.IsNullOrWhiteSpace(CreatorName) ? "" : $" · {CreatorName}");

    public string PlayCountBadge => PlayCount is > 0
        ? $"{FormatPlayCount(PlayCount.Value)}次播放"
        : (MusicCount > 0 ? $"{MusicCount}首" : "精选");

    public static string FormatPlayCount(long count) => count switch
    {
        >= 100_000_000 => $"{(count / 100_000_000.0):0.#}亿",
        >= 10_000 => $"{(count / 10_000.0):0.#}万",
        > 0 => count.ToString(),
        _ => ""
    };
}

