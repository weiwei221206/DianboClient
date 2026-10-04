using Dianbo.App.Services;
using Dianbo.Infrastructure.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Dianbo.App;

public sealed partial class MainWindow
{
    private TaskbarLyricOverlay? _taskbarOverlay;

    private void InitializeTaskbarLyrics()
    {
        _services.Settings.TaskbarLyrics ??= new TaskbarLyricSettings();
        try
        {
            _taskbarOverlay = new TaskbarLyricOverlay(
                _services.Settings.TaskbarLyrics,
                DispatcherQueue,
                OpenTaskbarLyricSettings,
                RestoreTaskbarMainWindow,
                () => _ = _services.Coordinator.PreviousAsync(_lifetime.Token),
                () => _ = TogglePauseAsync(),
                () => _ = _services.Coordinator.NextAsync(_lifetime.Token),
                position => _ = _services.Coordinator.SeekAsync(position, _lifetime.Token),
                volume =>
                {
                    _services.Settings.Volume = volume;
                    _services.SaveSettings();
                    _ = _services.Coordinator.SetVolumeAsync(volume, _lifetime.Token);
                },
                _services.Log.Write,
                () => ThemeHelper.IsDark);
            SyncTaskbarLyrics();
        }
        catch (Exception ex)
        {
            _services.Log.Write($"taskbar lyric initialization failed: {ex}");
        }
    }

    private void SyncTaskbarLyrics()
    {
        _taskbarOverlay?.Update(_services.Coordinator.Snapshot,
            _services.Coordinator.CurrentSong, _services.Coordinator.Lyrics);
    }

    private void OpenTaskbarLyricSettings()
    {
        RestoreTaskbarMainWindow();
        NavigateSettings("settings-taskbar-lyrics", recordHistory: false);
    }

    private void RestoreTaskbarMainWindow()
    {
        if (_trayService is not null) _trayService.RestoreAndActivateWindow();
        else { AppWindow.Show(); Activate(); }
    }

    private void ApplyTaskbarSettingsToUi()
    {
        var previous = _applyingSettings;
        _applyingSettings = true;
        try
        {
            var s = _services.Settings.TaskbarLyrics ??= new TaskbarLyricSettings();
            TaskbarEnabledToggle.IsOn = s.Enabled;
            TaskbarLyricsStatusText.Text = s.Enabled ? "已启用" : "已关闭";
            TaskbarPositionCombo.SelectedIndex = s.PositionMode switch { "center" => 1, "weather_right" => 2, _ => 0 };
            TaskbarWidthSlider.Value = Math.Clamp(s.Width, 180, 700);
            TaskbarXOffsetSlider.Value = Math.Clamp(s.XOffset, -200, 400);
            TaskbarYOffsetSlider.Value = Math.Clamp(s.YOffset, -30, 30);
            TaskbarCoverToggle.IsOn = s.ShowCover;
            TaskbarRotateToggle.IsOn = s.RotateCover;
            TaskbarTranslationToggle.IsOn = s.ShowTranslation;
            TaskbarColorCombo.SelectedIndex = s.ColorMode switch { "white" => 1, "black" => 2, "custom" => 3, _ => 0 };
            TaskbarCustomColorBox.Text = s.CustomColor ?? string.Empty;
            TaskbarFontBox.Text = s.FontFamily ?? string.Empty;
            TaskbarMainFontSlider.Value = Math.Clamp(s.MainFontSize, 10, 24);
            TaskbarSubFontSlider.Value = Math.Clamp(s.SubFontSize, 9, 20);
            TaskbarCharacterSpacingSlider.Value = Math.Clamp(s.CharacterSpacing, -3, 1);
            TaskbarAnimationCombo.SelectedIndex = s.AnimationType switch
            {
                "SlideFade" => 1,
                "FadeOnly" => 2,
                "None" => 3,
                _ => 0
            };
            TaskbarAnimationDurationSlider.Value = Math.Clamp(s.AnimationDurationMs, 100, 600);
            TaskbarAnimationDurationSlider.IsEnabled = s.AnimationType != "None";
            TaskbarBackgroundToggle.IsOn = s.ShowBackgroundCard;
            TaskbarAutoHideToggle.IsOn = s.AutoHideWithTaskbar;
            TaskbarFullscreenToggle.IsOn = s.HideWhenFullscreen;
            UpdateTaskbarSliderLabels(s);
            UpdateTaskbarInputErrors();
        }
        finally { _applyingSettings = previous; }
    }

    private void TaskbarSettings_Changed(object sender, RoutedEventArgs e) => SaveTaskbarSettingsFromUi();
    private void TaskbarSlider_Changed(object sender, RangeBaseValueChangedEventArgs e) => SaveTaskbarSettingsFromUi();
    private void TaskbarText_LostFocus(object sender, RoutedEventArgs e) => SaveTaskbarSettingsFromUi();
    private void TaskbarText_Changed(object sender, TextChangedEventArgs e) => UpdateTaskbarInputErrors();

    private void UpdateTaskbarInputErrors()
    {
        if (TaskbarCustomColorBox is null || TaskbarFontBox is null
            || TaskbarCustomColorError is null || TaskbarFontError is null) return;
        var isCustomColor = (TaskbarColorCombo?.SelectedItem as ComboBoxItem)?.Tag as string == "custom";
        TaskbarCustomColorBox.IsEnabled = isCustomColor;
        TaskbarCustomColorError.Visibility = isCustomColor && !TaskbarLyricAppearance.TryParseRgb(TaskbarCustomColorBox.Text.Trim(), out _)
            ? Visibility.Visible : Visibility.Collapsed;
        var fontText = TaskbarFontBox.Text.Trim();
        var fontValid = string.IsNullOrEmpty(fontText) || TaskbarLyricAppearance.IsInstalledFont(fontText);
        TaskbarFontError.Visibility = fontValid ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveTaskbarSettingsFromUi()
    {
        if (_applyingSettings || TaskbarEnabledToggle is null) return;
        var s = _services.Settings.TaskbarLyrics ??= new TaskbarLyricSettings();
        s.Enabled = TaskbarEnabledToggle.IsOn;
        s.PositionMode = (TaskbarPositionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "left";
        s.Width = (int)Math.Round(TaskbarWidthSlider.Value);
        s.XOffset = (int)Math.Round(TaskbarXOffsetSlider.Value);
        s.YOffset = (int)Math.Round(TaskbarYOffsetSlider.Value);
        s.ShowCover = TaskbarCoverToggle.IsOn;
        s.RotateCover = TaskbarRotateToggle.IsOn;
        s.ShowTranslation = TaskbarTranslationToggle.IsOn;
        s.ColorMode = (TaskbarColorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";
        var colorText = TaskbarCustomColorBox.Text.Trim();
        if (TaskbarLyricAppearance.TryParseRgb(colorText, out _))
            s.CustomColor = colorText.ToUpperInvariant();
        var fontText = TaskbarFontBox.Text.Trim();
        if (string.IsNullOrEmpty(fontText))
            s.FontFamily = TaskbarLyricAppearance.DefaultFontFamily;
        else if (TaskbarLyricAppearance.IsInstalledFont(fontText))
            s.FontFamily = fontText;
        s.MainFontSize = TaskbarMainFontSlider.Value;
        s.SubFontSize = TaskbarSubFontSlider.Value;
        s.CharacterSpacing = TaskbarCharacterSpacingSlider.Value;
        s.AnimationType = (TaskbarAnimationCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "HorizontalSweep";
        s.AnimationDurationMs = (int)Math.Round(TaskbarAnimationDurationSlider.Value);
        TaskbarAnimationDurationSlider.IsEnabled = s.AnimationType != "None";
        s.ShowBackgroundCard = TaskbarBackgroundToggle.IsOn;
        s.AutoHideWithTaskbar = TaskbarAutoHideToggle.IsOn;
        s.HideWhenFullscreen = TaskbarFullscreenToggle.IsOn;
        TaskbarLyricsStatusText.Text = s.Enabled ? "已启用" : "已关闭";
        UpdateTaskbarSliderLabels(s);
        UpdateTaskbarInputErrors();
        _services.SaveSettings();
        _taskbarOverlay?.ApplySettings(s);
    }

    private void UpdateTaskbarSliderLabels(TaskbarLyricSettings s)
    {
        TaskbarWidthText.Text = $"当前 {s.Width} 像素 · 默认值 360 像素";
        TaskbarXOffsetText.Text = $"当前 {s.XOffset:+0;-0;0} 像素 · 默认值 +12 像素";
        TaskbarYOffsetText.Text = $"当前 {s.YOffset:+0;-0;0} 像素 · 默认值 0 像素";
        TaskbarMainFontText.Text = $"当前 {s.MainFontSize:0} · 默认值 15";
        TaskbarSubFontText.Text = $"当前 {s.SubFontSize:0} · 默认值 12";
        TaskbarCharacterSpacingText.Text = $"当前 {s.CharacterSpacing:+0.##;-0.##;0} 像素 · 默认值 -0.5 像素";
        TaskbarAnimationDurationText.Text = $"当前 {s.AnimationDurationMs} 毫秒 · 默认值 400 毫秒";
    }
}
