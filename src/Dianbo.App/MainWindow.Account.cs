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
    private QrLoginDialog? _qrDialog;

    private async void Login_Click(object sender, RoutedEventArgs e) => await ShowQrLoginAsync();

    /// <summary>打开二维码登录窗口</summary>
    private async Task ShowQrLoginAsync()
    {
        if (_services.Auth.IsSignedIn)
        {
            await ShowDialogAsync("已经登录", $"当前账号 UID {_services.Auth.Current?.Uid}。如需更换账号，请先退出登录。");
            return;
        }
        //同时只允许一个
        if (_qrDialog is not null) return;

        var dialog = new QrLoginDialog(_services, _dispatcher, Root.XamlRoot, _lifetime.Token);
        _qrDialog = dialog;
        try
        {
            dialog.Start();
            await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            _services.Log.Write($"qr dialog failed: {exception.GetType().Name}: {exception.Message}");
            await ShowDialogAsync("无法打开登录窗口", exception.Message);
        }
        finally
        {
            _qrDialog = null;
            UpdateAccountUi();
            if (_services.Auth.IsSignedIn)
            {
                _ = PopulateSidebarPlaylistsAsync();
            }
        }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.RequestedTheme,
            Title = "退出登录",
            Content = "将清除本机保存的会话并停止播放。这不会注销其他设备。",
            PrimaryButtonText = "退出登录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        CancelPlaylistDetailLoad();
        await _services.Coordinator.StopAsync(CancellationToken.None);
        await _services.Auth.SignOutAsync(CancellationToken.None);
        _userPlaylists.Clear();
        _fondSongs.Clear();
        _detailPlaylistSongs.Clear();
        _selectedPlaylist = null;
        _currentFondPlaylist = null;
        ResetSidebarLibraryItems();
        UpdateAccountUi();
        UpdateLibRootUi();
        UpdateLibFondUi();
        if (_currentPage == "library") Navigate("library");
    }

    private string? _currentAvatarUrl;

    private void UpdateAccountUi()
    {
        var session = _services.Auth.Current;
        var profile = _services.Auth.Profile;
        var signedIn = session is not null;

        var nickname = profile?.Nickname;
        var displayName = !string.IsNullOrWhiteSpace(nickname)
            ? nickname
            : (signedIn ? $"已登录 · UID {session!.Uid}" : "未登录");

        var vipInfo = profile?.VipStatusText ?? (signedIn ? "会员状态获取中..." : "登录后同步喜欢的音乐");

        AccountStatusText.Text = signedIn
            ? $"已登录：{(!string.IsNullOrWhiteSpace(nickname) ? $"{nickname}（UID {session!.Uid}）" : $"UID {session!.Uid}")} · {profile?.VipStatusText ?? "会员状态获取中"}"
            : "尚未登录。播放受权益限制的歌曲前需要登录。";
        AccountStateTitle.Text = signedIn
            ? (!string.IsNullOrWhiteSpace(nickname) ? $"{nickname} · UID {session!.Uid}" : $"已登录 · UID {session!.Uid}")
            : "未登录";
        AccountStateDetail.Text = signedIn ? vipInfo : "登录后同步喜欢的音乐并恢复会话";
        PaneAccountTitle.Text = displayName;
        PaneAccountDetail.Text = signedIn ? vipInfo : "登录后同步喜欢的音乐";
        ToolTipService.SetToolTip(PaneAccount, signedIn ? $"{displayName}\n{vipInfo}" : "登录与账号");
        SignInButton.IsEnabled = !signedIn;
        SignOutButton.IsEnabled = signedIn;
        SessionLocationText.Text = $"会话位置：{_services.SessionStore.Location}（当前用户加密）；日志不记录票据或账号原值。";

        UpdateAvatarUi(signedIn ? profile?.AvatarUrl : null);
    }

    private void UpdateAvatarUi(string? avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            _currentAvatarUrl = null;
            PaneAccountAvatarBorder.ClearValue(Border.BackgroundProperty);
            PaneAccountAvatarIcon.Visibility = Visibility.Visible;

            AccountStateAvatarBorder.ClearValue(Border.BackgroundProperty);
            AccountStateAvatarIcon.Visibility = Visibility.Visible;
            return;
        }

        if (string.Equals(avatarUrl, _currentAvatarUrl, StringComparison.Ordinal))
        {
            return;
        }

        _currentAvatarUrl = avatarUrl;
        var source = CreateAvatarSource(avatarUrl);
        if (source is BitmapImage bmp)
        {
            var brush = new ImageBrush { ImageSource = bmp, Stretch = Stretch.UniformToFill };

            bmp.ImageOpened += (_, _) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    PaneAccountAvatarBorder.Background = brush;
                    PaneAccountAvatarIcon.Visibility = Visibility.Collapsed;
                    AccountStateAvatarBorder.Background = brush;
                    AccountStateAvatarIcon.Visibility = Visibility.Collapsed;
                });
            };
            bmp.ImageFailed += (_, _) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    PaneAccountAvatarBorder.ClearValue(Border.BackgroundProperty);
                    PaneAccountAvatarIcon.Visibility = Visibility.Visible;
                    AccountStateAvatarBorder.ClearValue(Border.BackgroundProperty);
                    AccountStateAvatarIcon.Visibility = Visibility.Visible;
                });
            };

            PaneAccountAvatarBorder.Background = brush;
            PaneAccountAvatarIcon.Visibility = Visibility.Collapsed;
            AccountStateAvatarBorder.Background = brush;
            AccountStateAvatarIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            PaneAccountAvatarBorder.ClearValue(Border.BackgroundProperty);
            PaneAccountAvatarIcon.Visibility = Visibility.Visible;

            AccountStateAvatarBorder.ClearValue(Border.BackgroundProperty);
            AccountStateAvatarIcon.Visibility = Visibility.Visible;
        }
    }

    private static ImageSource? CreateAvatarSource(string? avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl)) return null;
        if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri)) return null;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 88, DecodePixelHeight = 88 };
            bitmap.UriSource = uri;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

}
