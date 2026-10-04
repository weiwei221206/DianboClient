using Dianbo.Core.Services;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace Dianbo.Infrastructure.Auth;

public sealed class ZxingQrCodeRenderer : IQrCodeRenderer
{
    public QrCodeImage Render(string content, int size)
    {
        if (string.IsNullOrEmpty(content) || size <= 0) return QrCodeImage.Empty;
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Width = size,
                Height = size,
                Margin = 1,
                CharacterSet = "UTF-8",
                ErrorCorrection = ErrorCorrectionLevel.M
            }
        };
        var pixelData = writer.Write(content);
        return pixelData is null || pixelData.Pixels is null
            ? QrCodeImage.Empty
            : new QrCodeImage(pixelData.Width, pixelData.Height, pixelData.Pixels);
    }
}
