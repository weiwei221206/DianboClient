using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Dianbo.App;

public static class ThemeHelper
{
    private static ElementTheme _actualTheme = ElementTheme.Light;

    public static ElementTheme ActualTheme
    {
        get => _actualTheme;
        set => _actualTheme = value;
    }

    public static bool IsDark => _actualTheme == ElementTheme.Dark;


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
