using System.Collections.ObjectModel;
using Dianbo.App.Services;
using Dianbo.App.ViewModels;
using Dianbo.Core.Models;
using Dianbo.Core.Playback;
using Dianbo.Core.Services;
using Dianbo.Infrastructure.Playback;
using Dianbo.Infrastructure.Storage;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace Dianbo.App;

public sealed partial class MainWindow : Window
{
    private async Task ShowDialogAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.RequestedTheme,
            Title = title,
            Content = content,
            CloseButtonText = "知道了"
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // 已有对话框打开或窗口正在关闭时忽略提示。
        }
    }

    private async Task<bool> ConfirmAsync(string title, string content, string confirmText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.RequestedTheme,
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<string?> PromptInputAsync(string title, string placeholder, string confirmText)
    {
        var textBox = new TextBox
        {
            PlaceholderText = placeholder,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 12, 0, 0)
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.RequestedTheme,
            Title = title,
            Content = textBox,
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        try
        {
            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                return textBox.Text.Trim();
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
