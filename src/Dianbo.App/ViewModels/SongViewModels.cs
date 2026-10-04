using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Dianbo.Core.Lyrics;
using Dianbo.Core.Models;
using Dianbo.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Dianbo.App;

namespace Dianbo.App.ViewModels;

public sealed class SongViewModel : INotifyPropertyChanged
{
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush FavoritedBrush = new(Windows.UI.Color.FromArgb(255, 235, 71, 71)); // #EB4747
    private bool _isFavorite;

    public SongViewModel(Song song, bool isFavorite = false)
    {
        Song = song;
        _isFavorite = isFavorite;
    }

    public Song Song { get; }
    public long Id => Song.Id;
    public string Name => Song.Name;
    public string Artist => string.IsNullOrWhiteSpace(Song.Artist) ? "未知歌手" : Song.Artist;
    public string? CoverUrl => Song.CoverUrl;
    public string DurationText => Song.Duration is { TotalSeconds: > 0 } duration
        ? $"{(int)duration.TotalMinutes}:{duration.Seconds:00}"
        : string.Empty;

    private int _queueIndex;
    private bool _isCurrentSong;

    public int QueueIndex
    {
        get => _queueIndex;
        set
        {
            if (_queueIndex == value) return;
            _queueIndex = value;
            Raise(nameof(QueueIndex));
            Raise(nameof(DisplayIndex));
        }
    }

    public string DisplayIndex => (_queueIndex + 1).ToString("D2");

    public bool IsCurrentSong
    {
        get => _isCurrentSong;
        set
        {
            if (_isCurrentSong == value) return;
            _isCurrentSong = value;
            Raise(nameof(IsCurrentSong));
            Raise(nameof(PlayingIndicatorVisibility));
            Raise(nameof(IndexVisibility));
            Raise(nameof(TitleForeground));
            Raise(nameof(TitleFontWeight));
        }
    }

    public Visibility PlayingIndicatorVisibility => _isCurrentSong ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IndexVisibility => _isCurrentSong ? Visibility.Collapsed : Visibility.Visible;

    public Microsoft.UI.Xaml.Media.Brush TitleForeground => _isCurrentSong
        ? ThemeHelper.GetAccentBrush()
        : ThemeHelper.GetTextPrimaryBrush();

    public Windows.UI.Text.FontWeight TitleFontWeight => _isCurrentSong
        ? Microsoft.UI.Text.FontWeights.SemiBold
        : Microsoft.UI.Text.FontWeights.Normal;

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            Raise(nameof(IsFavorite));
            Raise(nameof(FavoriteGlyph));
            Raise(nameof(FavoriteTooltip));
            Raise(nameof(FavoriteBrush));
        }
    }

    public string FavoriteGlyph => _isFavorite ? "\uEB52" : "\uEB51";
    public string FavoriteTooltip => _isFavorite ? "取消收藏" : "添加收藏";

    public Microsoft.UI.Xaml.Media.Brush FavoriteBrush => _isFavorite
        ? FavoritedBrush
        : ThemeHelper.GetFavoriteUncheckedBrush();

    public void RefreshThemeBrushes()
    {
        Raise(nameof(TitleForeground));
        Raise(nameof(FavoriteBrush));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>歌词列表</summary>
public sealed class LyricLineViewModel : INotifyPropertyChanged
{
    private bool _isCurrent;
    private bool _isHovered;

    public LyricLineViewModel(int index, LyricGroup? group, string? unpaired)
    {
        Index = index;
        Group = group;
        Unpaired = unpaired;
    }

    public int Index { get; }
    public LyricGroup? Group { get; }
    public string? Unpaired { get; }
    public long StartTimeMs => Group?.StartTimeMs ?? 0;
    public string Timestamp => $"{StartTimeMs / 60_000:00}:{StartTimeMs / 1_000 % 60:00}";
    public Visibility TimestampVisibility => Group is not null && IsHovered ? Visibility.Visible : Visibility.Collapsed;
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (_isHovered == value) return;
            _isHovered = value;
            Raise(nameof(IsHovered));
            Raise(nameof(TimestampVisibility));
        }
    }
    public string Original => Group?.OriginalText ?? Unpaired ?? string.Empty;
    public string Translation => Group?.TranslationText ?? string.Empty;
    /// <summary>由“显示翻译歌词”设置控制。</summary>
    public bool ShowTranslation { get; init; } = true;

    public Visibility TranslationVisibility =>
        string.IsNullOrEmpty(Translation) || !ShowTranslation ? Visibility.Collapsed : Visibility.Visible;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            Raise(nameof(IsCurrent));
            Raise(nameof(FontSize));
            Raise(nameof(Foreground));
            Raise(nameof(FontWeight));
            Raise(nameof(Opacity));
        }
    }

    public double FontSize => IsCurrent ? 22 : 15;

    public double Opacity => IsCurrent ? 1.0 : 0.45;

    public Microsoft.UI.Xaml.Media.Brush Foreground => IsCurrent
        ? ThemeHelper.GetTextPrimaryBrush()
        : ThemeHelper.GetTextSecondaryBrush();

    public void RefreshThemeBrushes()
    {
        Raise(nameof(Foreground));
    }

    public Windows.UI.Text.FontWeight FontWeight => IsCurrent
        ? Microsoft.UI.Text.FontWeights.Bold
        : Microsoft.UI.Text.FontWeights.Normal;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

