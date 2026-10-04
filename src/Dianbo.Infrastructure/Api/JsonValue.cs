using System.Text.Json;

namespace Dianbo.Infrastructure.Api;

public static class JsonValue
{
    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    public static JsonElement? GetProperty(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) ? value : null;

    public static string? AsString(JsonElement? element)
    {
        if (element is not { } value) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static long? AsLong(JsonElement? element)
    {
        if (element is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var number)) return number;
            if (value.TryGetDouble(out var floating)) return (long)floating;
            return null;
        }
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }

    public static int? AsInt(JsonElement? element)
    {
        var number = AsLong(element);
        if (number is null) return null;
        return number.Value is > int.MaxValue or < int.MinValue ? null : (int)number.Value;
    }

    public static bool? AsBool(JsonElement? element)
    {
        if (element is not { } value) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetDouble() != 0,
            JsonValueKind.String => value.GetString() == "1" || string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => null
        };
    }

    public static double? AsDouble(JsonElement? element)
    {
        if (element is not { } value) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }
}
