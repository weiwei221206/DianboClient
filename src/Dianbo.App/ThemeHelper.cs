using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Dianbo.App;

public static class ThemeHelper
{
    private static ElementTheme _actualTheme = ElementTheme.Light;
    private static string _configuredTheme = "Default";

    public static string ConfiguredTheme
    {
        get => _configuredTheme;
        set
        {
            if (_configuredTheme != value)
            {
                _configuredTheme = value ?? "Default";
                ThemeChanged?.Invoke();
            }
        }
    }

    public static ElementTheme ActualTheme
    {
        get => _actualTheme;
        set
        {
            if (_actualTheme != value)
            {
                _actualTheme = value;
                ThemeChanged?.Invoke();
            }
        }
    }

    public static event Action? ThemeChanged;

    public static bool IsDark => ResolveIsDark(_configuredTheme);

    public static bool ResolveIsDark(string? configuredTheme = null)
    {
        var theme = configuredTheme ?? _configuredTheme;
        if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)) return false;

        if (_actualTheme == ElementTheme.Dark) return true;
        if (_actualTheme == ElementTheme.Light && !string.Equals(theme, "Default", StringComparison.OrdinalIgnoreCase)) return false;

        return IsSystemAppDark();
    }

    public static bool IsSystemAppDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int appsUseLight)
                return appsUseLight == 0;
            if (key?.GetValue("SystemUsesLightTheme") is int sysUseLight)
                return sysUseLight == 0;
        }
        catch (Exception)
        {
        }

        try
        {
            if (Application.Current?.RequestedTheme == ApplicationTheme.Dark)
                return true;
        }
        catch (Exception)
        {
        }

        return false;
    }


    private static readonly SolidColorBrush DarkPrimary = new(Colors.White);
    private static readonly SolidColorBrush LightPrimary = new(Windows.UI.Color.FromArgb(228, 0, 0, 0));

    private static readonly SolidColorBrush DarkSecondary = new(Windows.UI.Color.FromArgb(200, 255, 255, 255));
    private static readonly SolidColorBrush LightSecondary = new(Windows.UI.Color.FromArgb(160, 0, 0, 0));

    private static readonly SolidColorBrush DarkFavoriteUnchecked = new(Windows.UI.Color.FromArgb(240, 240, 240, 240));
    private static readonly SolidColorBrush LightFavoriteUnchecked = new(Windows.UI.Color.FromArgb(180, 85, 85, 85));

    public static Brush GetTextPrimaryBrush() =>
        IsDark ? DarkPrimary : LightPrimary;

    public static Brush GetTextSecondaryBrush() =>
        IsDark ? DarkSecondary : LightSecondary;

    public static Brush GetFavoriteUncheckedBrush() =>
        IsDark ? DarkFavoriteUnchecked : LightFavoriteUnchecked;

    public static Brush GetAccentBrush()
    {
        if (Application.Current?.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var accent) == true && accent is Brush ab)
        {
            return ab;
        }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
    }
}
