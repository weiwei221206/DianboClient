using System.Drawing;

namespace Dianbo.App.Services;

internal static class TaskbarLyricAppearance
{
    public const string DefaultFontFamily = "Microsoft YaHei UI";

    private static readonly Lazy<HashSet<string>> InstalledFonts = new(() =>
        new HashSet<string>(FontFamily.Families.Select(family => family.Name), StringComparer.OrdinalIgnoreCase));

    public static bool IsInstalledFont(string? name) =>
        !string.IsNullOrWhiteSpace(name) && InstalledFonts.Value.Contains(name.Trim());

    public static bool TryParseRgb(string? value, out Color color)
    {
        color = Color.Empty;
        if (value is null || value.Length != 7 || value[0] != '#') return false;
        for (var i = 1; i < value.Length; i++)
        {
            var digit = value[i];
            if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f')
                || (digit >= 'A' && digit <= 'F'))) return false;
        }
        color = Color.FromArgb(
            Convert.ToByte(value.Substring(1, 2), 16),
            Convert.ToByte(value.Substring(3, 2), 16),
            Convert.ToByte(value.Substring(5, 2), 16));
        return true;
    }
}
