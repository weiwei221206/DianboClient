using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dianbo.Core.Lyrics;

public static partial class LyricParser
{
    private const int MaxEntries = 20000;

    [GeneratedRegex(@"\[(?<m>\d{1,3}):(?<s>[0-5]?\d)(?:[.:](?<f>\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimeTagRegex();

    [GeneratedRegex(@"<(?:-?\d+)(?:,-?\d+)*>", RegexOptions.Compiled)]
    private static partial Regex InlineTagRegex();

    [GeneratedRegex(@"^\[(?<key>[a-zA-Z]+):(?<value>.*)\]$", RegexOptions.Compiled)]
    private static partial Regex MetadataRegex();

    public static LyricDocument Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return LyricDocument.Empty;

        long sourceOffsetMs = 0;
        string? title = null;
        var entries = new List<Entry>();
        var unpaired = new List<string>();
        var sequence = 0;
        var order = 0;

        foreach (var rawLine in SplitLines(content))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var metadata = MetadataRegex().Match(line);
            if (metadata.Success)
            {
                var key = metadata.Groups["key"].Value.ToLowerInvariant();
                var value = metadata.Groups["value"].Value.Trim();
                if (key == "offset")
                {
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        sourceOffsetMs = parsed;
                }
                else if (key == "ti" && title is null)
                {
                    title = value;
                }
                continue;
            }

            var stamps = TimeTagRegex().Matches(line);
            var text = StripTags(line);
            if (stamps.Count == 0)
            {
                if (text.Length > 0) entries.Add(new Entry(null, text, sequence++, order++, false));
                continue;
            }

            foreach (Match stamp in stamps)
            {
                entries.Add(new Entry(ToMilliseconds(stamp), text, sequence++, order++, line.Contains("<0,0>", StringComparison.Ordinal)));
                if (entries.Count > MaxEntries) break;
            }
        }

        if (entries.Count == 0)
        {
            return new LyricDocument(Array.Empty<LyricGroup>(), sourceOffsetMs, unpaired, title);
        }

        var ordered = entries.OrderBy(entry => entry.Order).ThenBy(entry => entry.StartMs ?? long.MaxValue);
        var groups = new List<LyricGroup>();
        var indexByStart = new Dictionary<long, int>();
        var pendingText = new List<string>();

        foreach (var entry in ordered)
        {
            if (entry.StartMs is null)
            {
                pendingText.Add(entry.Text);
                continue;
            }

            if (entry.IsExplicitTranslation)
            {
                if (groups.Count == 0)
                {
                    unpaired.AddRange(pendingText);
                    pendingText.Clear();
                    if (entry.Text.Length > 0) unpaired.Add(entry.Text);
                    continue;
                }

                var last = groups.Count - 1;
                var group = groups[last];
                var translations = group.TranslationLines.ToList();
                translations.AddRange(pendingText);
                if (entry.Text.Length > 0 && !translations.Contains(entry.Text)) translations.Add(entry.Text);
                groups[last] = group with { TranslationLines = translations };
                pendingText.Clear();
                continue;
            }

            if (indexByStart.TryGetValue(entry.StartMs.Value, out var existing))
            {
                var group = groups[existing];
                if (!group.OriginalLines.Contains(entry.Text))
                {
                    group = group with { OriginalLines = [.. group.OriginalLines, entry.Text] };
                }
                if (pendingText.Count > 0 && group.TranslationLines.Count == 0)
                {
                    group = group with { TranslationLines = [.. pendingText] };
                }
                pendingText.Clear();
                groups[existing] = group;
                continue;
            }

            var previousIndex = groups.Count - 1;
            if (previousIndex >= 0 && groups[previousIndex].StartTimeMs < entry.StartMs.Value && pendingText.Count > 0)
            {
                var previous = groups[previousIndex];
                groups[previousIndex] = previous with { TranslationLines = [.. previous.TranslationLines, .. pendingText] };
                pendingText.Clear();
            }
            else if (pendingText.Count > 0)
            {
                unpaired.AddRange(pendingText);
                pendingText.Clear();
            }

            indexByStart[entry.StartMs.Value] = groups.Count;
            groups.Add(new LyricGroup
            {
                StartTimeMs = entry.StartMs.Value,
                OriginalLines = [entry.Text],
                TranslationLines = []
            });
        }

        if (pendingText.Count > 0 && groups.Count > 0)
        {
            var last = groups.Count - 1;
            groups[last] = groups[last] with { TranslationLines = [.. groups[last].TranslationLines, .. pendingText] };
            pendingText.Clear();
        }
        unpaired.AddRange(pendingText);

        var orderedGroups = new List<LyricGroup>();
        var lastStart = long.MinValue;
        foreach (var group in groups.OrderBy(group => group.StartTimeMs))
        {
            if (group.OriginalLines.Count == 0 && group.TranslationLines.Count == 0) continue;
            if (group.StartTimeMs == lastStart) continue;
            lastStart = group.StartTimeMs;
            orderedGroups.Add(group with
            {
                OriginalLines = group.OriginalLines.Where(line => line.Length > 0).ToList(),
                TranslationLines = group.TranslationLines.Where(line => line.Length > 0).ToList()
            });
        }

        return new LyricDocument(orderedGroups, sourceOffsetMs, unpaired, title);
    }

    internal static bool Resembles(string original, string candidate)
    {
        if (string.Equals(original, candidate, StringComparison.Ordinal)) return false;
        var length = Math.Max(original.Length, candidate.Length);
        if (length == 0) return false;
        var difference = Math.Abs(original.Length - candidate.Length);
        return difference <= 1 + (length * 0.5);
    }

    public static string StripTags(string line)
    {
        var withoutInline = InlineTagRegex().Replace(line, string.Empty);
        var withoutTime = TimeTagRegex().Replace(withoutInline, string.Empty);
        return Normalize(withoutTime);
    }

    private static long ToMilliseconds(Match stamp)
    {
        var minutes = long.Parse(stamp.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(stamp.Groups["s"].Value, CultureInfo.InvariantCulture);
        var fractionText = stamp.Groups["f"].Value;
        long fraction = fractionText.Length switch
        {
            1 => long.Parse(fractionText, CultureInfo.InvariantCulture) * 100,
            2 => long.Parse(fractionText, CultureInfo.InvariantCulture) * 10,
            3 => long.Parse(fractionText, CultureInfo.InvariantCulture),
            _ => 0
        };
        return (minutes * 60_000) + (seconds * 1_000) + fraction;
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character == '\u3000' ? ' ' : character);
        }
        return builder.ToString().Trim();
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (character is '\n' or '\r')
            {
                yield return content[start..index];
                if (character == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
                start = index + 1;
            }
        }
        if (start < content.Length) yield return content[start..];
    }

    private readonly record struct Entry(long? StartMs, string Text, int Sequence, int Order, bool IsExplicitTranslation);
}
