namespace Dianbo.Core.Models;

public enum PlaybackMode
{
    Sequential,
    RepeatAll,
    RepeatOne,
    Shuffle
}

public sealed record OverlaySettings
{
    public bool Enabled { get; init; }
    public bool Locked { get; init; } = true;
    public double FontSize { get; init; } = 32;
    public double Left { get; init; } = 200;
    public double Top { get; init; } = 200;
    public double Width { get; init; } = 900;
    public double Height { get; init; } = 180;
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public bool ShowTranslation { get; init; } = true;
}
