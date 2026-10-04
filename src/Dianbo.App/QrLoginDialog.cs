using Dianbo.App.Services;
using Dianbo.Core.Models;
using Dianbo.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Dianbo.App;

/// 二维码登录对话框。二维码是临时登录材料：只在当前窗口显示，
public sealed class QrLoginDialog : ContentDialog
{
    private readonly AppServices _services;
    private readonly UiDispatcher _dispatcher;
    private readonly CancellationTokenSource _dialogCts;
    private CancellationTokenSource? _poll;
    private int _exchanging;
    private int _pollCount;
    private readonly Image _qrImage = new() { Width = 240, Height = 240 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _hint = new()
    {
        Text = "请使用 波点音乐 APP 扫码登录",
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        FontSize = 12
    };
    private readonly TextBox _qrContent = new()
    {
        IsReadOnly = true,
        AcceptsReturn = false,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Visibility = Visibility.Collapsed,
        Header = "二维码内容（无法扫码时可在手机上打开）"
    };
    private readonly ProgressRing _ring = new() { IsActive = true, Width = 20, Height = 20 };
    private readonly Button _refresh = new() { Content = "刷新二维码" };
    private readonly Button _reveal = new() { Content = "显示二维码内容" };

    public QrLoginDialog(AppServices services, UiDispatcher dispatcher, XamlRoot xamlRoot, CancellationToken lifetimeToken)
    {
        _services = services;
        _dispatcher = dispatcher;
        _dialogCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);

        Title = "登录点波音乐";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.None;
        RequestedTheme = (xamlRoot.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        XamlRoot = xamlRoot;

        var qrContainer = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _qrImage
        };

        var layout = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Width = 280 };
        layout.Children.Add(qrContainer);
        layout.Children.Add(_ring);
        layout.Children.Add(_status);
        layout.Children.Add(_hint);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_reveal);
        layout.Children.Add(buttons);
        layout.Children.Add(_qrContent);
        Content = layout;

        _refresh.Click += (_, _) => _ = StartAsync();
        _reveal.Click += (_, _) =>
        {
            _qrContent.Visibility = _qrContent.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            _reveal.Content = _qrContent.Visibility == Visibility.Visible ? "隐藏二维码内容" : "显示二维码内容";
        };
        Closed += (_, _) =>
        {
            try { _dialogCts.Cancel(); } catch (ObjectDisposedException) { }
        };
    }

    public void Start() => _ = StartAsync();

    private async Task StartAsync()
    {
        var previous = Interlocked.Exchange(ref _poll, null);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(_dialogCts.Token);
        _poll = pollCts;
        var token = pollCts.Token;
        Interlocked.Exchange(ref _pollCount, 0);
        Interlocked.Exchange(ref _exchanging, 0);

        try
        {
            SetStatus("正在创建二维码…");
            _ring.IsActive = true;
            _qrImage.Source = null;
            _qrContent.Text = string.Empty;

            var ticket = await _services.Auth.CreateQrCodeAsync(token);
            var pixels = _services.QrRenderer.Render(ticket.ScanUrl, 240);
            _qrImage.Source = await ToImageSourceAsync(pixels);
            _qrContent.Text = ticket.ScanUrl;
            if (_qrImage.Source is null)
            {
                SetStatus("二维码渲染失败，请点“显示二维码内容”后在手机上打开。");
                _ring.IsActive = false;
                return;
            }
            SetStatus("等待手机扫码确认…");

            var started = DateTimeOffset.UtcNow;
            var deadline = started.AddMinutes(3);
            while (!token.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    SetStatus("二维码已超时，请刷新后重试。");
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(3), token);
                var state = await _services.Auth.PollQrCodeAsync(ticket.QrCode, token);
                var count = Interlocked.Increment(ref _pollCount);
                switch (state)
                {
                    case QrPollState.Waiting:
                        var waited = (int)(DateTimeOffset.UtcNow - started).TotalSeconds;
                        SetStatus(waited < 30
                            ? $"等待手机扫码确认…（已等待 {waited} 秒）"
                            : $"仍在等待手机确认：已 {waited} 秒 / {count} 次轮询。"
                              + "手机端若提示失败，请确认是在「波点音乐」App 内扫码、手机网络正常，然后点“刷新二维码”重试。");
                        continue;
                    case QrPollState.Unusable:
                        SetStatus("二维码不可用，请刷新。");
                        break;
                    case QrPollState.Confirmed:
                        if (Interlocked.Exchange(ref _exchanging, 1) != 0) return;
                        SetStatus("已确认，正在完成登录…");
                        var session = await _services.Auth.ExchangeQrCodeAsync(ticket.QrCode, token);
                        if (session is null)
                        {
                            SetStatus("登录兑换失败，请刷新二维码重试。");
                            break;
                        }
                        SetStatus(_services.Auth.SessionPersistenceFailed
                            ? $"登录成功：UID {session.Uid}（本次运行可用，但会话没能写入本机，下次启动需要重新登录）。"
                            : $"登录成功：UID {session.Uid}（票据不显示）。");
                        await Task.Delay(500, CancellationToken.None);
                        _dispatcher.Post(Hide);
                        return;
                    default:
                        break;
                }
                _ring.IsActive = false;
                return;
            }
            _ring.IsActive = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (BodianApiException exception)
        {
            _ring.IsActive = false;
            SetStatus($"二维码流程失败：{exception.Message}");
        }
        catch (Exception exception)
        {
            _ring.IsActive = false;
            _services.Log.Write($"qr login failed: {exception.GetType().Name}: {exception.Message}");
            SetStatus($"二维码流程失败：{exception.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _poll, null, pollCts);
        }
    }

    private void SetStatus(string text) => _dispatcher.Post(() => _status.Text = text);

    private static async Task<ImageSource?> ToImageSourceAsync(QrCodeImage image)
    {
        if (image.Width <= 0 || image.Height <= 0) return null;
        var expected = (long)image.Width * image.Height * 4;
        if (image.Pixels.LongLength < expected) return null;

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)image.Width,
                (uint)image.Height,
                96,
                96,
                image.Pixels);
            await encoder.FlushAsync();
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
