using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Dianbo.Core.Models;
using Microsoft.UI.Dispatching;

namespace Dianbo.App.Services;

// The popup is a separate per-pixel-alpha HWND. It shares the lyric renderer's
// thread and cover bitmap, but never changes the lyric strip's window bounds.
internal sealed class TaskbarLyricNativePopup : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x80000;
    private const int WsExToolWindow = 0x80;
    private const int WsExTopmost = 0x8;
    private const int WmActivate = 0x0006;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseLeave = 0x02A3;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmCaptureChanged = 0x0215;
    private const int WmSetCursor = 0x0020;
    private const int WmKeyDown = 0x0100;
    private const int WmClose = 0x0010;
    private const int WmNcDestroy = 0x0082;
    private const uint SwpNoActivate = 0x10;
    private const uint SwpShowWindow = 0x40;
    private const uint SwpHideWindow = 0x80;

    private readonly WndProc _wndProc;
    private readonly string _className = "DianboLyricPopup_" + Guid.NewGuid().ToString("N");
    private readonly DispatcherQueue _ui;
    private readonly Action _openSettings;
    private readonly Action _openApp;
    private readonly Action _previous;
    private readonly Action _togglePlay;
    private readonly Action _next;
    private readonly Action<TimeSpan> _seek;
    private readonly Action<string> _log;
    private IntPtr _hwnd;
    private IntPtr _surfaceDc, _surfaceDib, _surfaceOld;
    private Bitmap? _surface;
    private Font? _titleFont, _artistFont, _symbolFont;
    private float _fontDpi;
    private Song? _song;
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Initial;
    private Bitmap? _cover;
    private bool _dark;
    private bool _open;
    private bool _closing;
    private bool _pendingDismiss;
    private bool _everActivated;
    private bool _dragging;
    private bool _dirty;
    private int _hover = -1;
    private int _pressed = -1;
    private double _dragRatio;
    private float _dpi = 1f;
    private int _x, _y, _width, _height;
    private IntPtr _foregroundAtOpen;
    private long _openedAt, _closingAt, _lastHiddenAt, _lastProgressFrame, _snapshotTick;

    public TaskbarLyricNativePopup(IntPtr owner, DispatcherQueue ui, Action openSettings,
        Action openApp, Action previous, Action togglePlay, Action next,
        Action<TimeSpan> seek, Action<string> log)
    {
        _ui = ui;
        _openSettings = openSettings;
        _openApp = openApp;
        _previous = previous;
        _togglePlay = togglePlay;
        _next = next;
        _seek = seek;
        _log = log;
        _wndProc = WindowProc;
        var wc = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WindowProc = _wndProc,
            Instance = GetModuleHandle(null),
            ClassName = _className
        };
        if (RegisterClassEx(ref wc) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _hwnd = CreateWindowEx(WsExLayered | WsExToolWindow | WsExTopmost,
            _className, "点波音乐迷你控制", WsPopup, 0, 0, 1, 1,
            owner, IntPtr.Zero, wc.Instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            UnregisterClass(_className, wc.Instance);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public bool IsOpen => _open;

    public void Toggle(TaskbarLyricPopupAnchor anchor, bool? dark = null)
    {
        if (dark.HasValue && _dark != dark.Value)
        {
            _dark = dark.Value;
            _dirty = true;
        }
        if (_open)
        {
            BeginClose();
            return;
        }
        if (Environment.TickCount64 - _lastHiddenAt < 220) return;
        _dpi = anchor.Dpi;
        _width = Math.Max(1, (int)Math.Round(308 * _dpi));
        _height = Math.Max(1, (int)Math.Round(142 * _dpi));
        _x = Math.Clamp(anchor.CoverX - (int)(14 * _dpi), anchor.MonitorLeft,
            Math.Max(anchor.MonitorLeft, anchor.MonitorRight - _width));
        _y = anchor.TaskbarTop < anchor.MonitorTop + _height + 8
            ? anchor.TaskbarBottom : anchor.TaskbarTop - _height;
        _y = Math.Clamp(_y, anchor.MonitorTop,
            Math.Max(anchor.MonitorTop, anchor.MonitorBottom - _height));
        _hover = _pressed = -1;
        _dragging = false;
        _closing = _pendingDismiss = false;
        _everActivated = false;
        _open = true;
        _openedAt = Environment.TickCount64;
        _dirty = true;
        SetWindowPos(_hwnd, new IntPtr(-1), _x, _y, _width, _height,
            SwpNoActivate | SwpShowWindow);
        Render(_openedAt);
        SetForegroundWindow(_hwnd);
        _foregroundAtOpen = GetForegroundWindow();
    }

    public void Update(Song? song, PlaybackSnapshot snapshot, Bitmap? cover, bool dark)
    {
        if (!ReferenceEquals(_song, song) || _snapshot != snapshot || !ReferenceEquals(_cover, cover) || _dark != dark)
            _dirty = true;
        if (_snapshot != snapshot) _snapshotTick = Environment.TickCount64;
        _song = song;
        _snapshot = snapshot;
        _cover = cover;
        _dark = dark;
    }

    public void FrameTick(long now)
    {
        if (!_open) return;
        if (_pendingDismiss && now - _openedAt >= 280)
        {
            _pendingDismiss = false;
            if (GetForegroundWindow() != _hwnd) BeginClose();
        }
        var foreground = GetForegroundWindow();
        if (!_everActivated && now - _openedAt >= 280
            && foreground != _hwnd && foreground != _foregroundAtOpen)
            BeginClose();
        if (_closing && now - _closingAt >= 140)
        {
            Hide();
            return;
        }
        if (now - _openedAt < 180 || _closing) _dirty = true;
        if (_snapshot.State == PlaybackState.Playing && !_snapshot.IsPaused
            && now - _lastProgressFrame >= 100)
        {
            _lastProgressFrame = now;
            _dirty = true;
        }
        if (_dirty) Render(now);
    }

    public void BeginClose()
    {
        if (!_open || _closing) return;
        _closing = true;
        _closingAt = Environment.TickCount64;
        _pressed = -1;
        _dragging = false;
        _dirty = true;
    }

    public void Hide()
    {
        if (!_open) return;
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            0x1 | 0x2 | SwpNoActivate | SwpHideWindow);
        _open = _closing = _pendingDismiss = _dragging = false;
        _lastHiddenAt = Environment.TickCount64;
    }

    private void Render(long now)
    {
        if (!_open || !EnsureSurface()) return;
        var entrance = Math.Clamp((now - _openedAt) / 180f, 0, 1);
        var eased = 1 - MathF.Pow(1 - entrance, 3);
        var exit = _closing ? Math.Clamp((now - _closingAt) / 140f, 0, 1) : 0;
        var progress = _closing ? 1 - exit * exit : eased;
        var scale = .94f + .06f * progress;
        using (var g = Graphics.FromImage(_surface!))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var centerX = _width / 2f;
            var bottom = _height - 8 * _dpi;
            g.TranslateTransform(centerX, bottom);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-centerX, -bottom);
            DrawCard(g, now);
        }
        var dst = new PointNative(_x, _y);
        var src = new PointNative(0, 0);
        var size = new SizeNative(_width, _height);
        var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = (byte)Math.Round(255 * progress), AlphaFormat = 1 };
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (!UpdateLayeredWindow(_hwnd, screen, ref dst, ref size, _surfaceDc, ref src, 0, ref blend, 2))
                _log($"taskbar popup present failed: {Marshal.GetLastWin32Error()}");
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        _dirty = false;
    }

    private void DrawCard(Graphics g, long now)
    {
        var d = _dpi;
        var card = new RectangleF(14 * d, 14 * d, 280 * d, 120 * d);
        for (var ring = 11; ring >= 1; ring--)
        {
            var grow = ring * .65f * d;
            using var shadowPath = Rounded(new RectangleF(card.Left - grow,
                card.Top - grow + 2 * d, card.Width + grow * 2,
                card.Height + grow * 2), (14 + ring * .65f) * d);
            using var shadow = new SolidBrush(Color.FromArgb(ring <= 4 ? 5 : 3, 0, 0, 0));
            g.FillPath(shadow, shadowPath);
        }
        using (var path = Rounded(card, 14 * d))
        using (var fill = new SolidBrush(_dark ? Color.FromArgb(175, 24, 24, 26) : Color.FromArgb(195, 255, 255, 255)))
        using (var border = new Pen(_dark ? Color.FromArgb(42, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0), d))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        var fg = _dark ? Color.White : Color.FromArgb(28, 28, 30);
        var secondary = _dark ? Color.FromArgb(175, 255, 255, 255) : Color.FromArgb(142, 142, 147);
        EnsureFonts(d);
        var cover = new RectangleF(card.Left + 14 * d, card.Top + 8 * d, 40 * d, 40 * d);
        using (var coverPath = new GraphicsPath())
        {
            coverPath.AddEllipse(cover);
            using var fill = new SolidBrush(_dark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0));
            g.FillPath(fill, coverPath);
            if (_cover is not null)
            {
                var saved = g.Save();
                g.SetClip(coverPath);
                g.DrawImage(_cover, cover);
                g.Restore(saved);
            }
            else
            {
                using var musicBrush = new SolidBrush(secondary);
                using var musicFont = new Font("Segoe UI Symbol", 21 * d, FontStyle.Regular, GraphicsUnit.Pixel);
                DrawCentered(g, "♪", musicFont, musicBrush, cover);
            }
            using var outline = new Pen(_dark ? Color.FromArgb(45, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0), d);
            g.DrawPath(outline, coverPath);
        }
        var textX = cover.Right + 12 * d;
        var textWidth = card.Right - 14 * d - 28 * d - textX;
        using var titleBrush = new SolidBrush(fg);
        using var subBrush = new SolidBrush(secondary);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(string.IsNullOrWhiteSpace(_song?.Name) ? "点波音乐" : _song.Name,
            _titleFont!, titleBrush, new RectangleF(textX, card.Top + 8 * d, textWidth, 21 * d), format);
        g.DrawString(_song is null ? "当前未在播放" : _song.Artist,
            _artistFont!, subBrush, new RectangleF(textX, card.Top + 29 * d, textWidth, 18 * d), format);

        for (var i = 0; i < 5; i++)
        {
            var rect = ButtonRect(i);
            if (_hover == i || _pressed == i)
            {
                using var hover = new SolidBrush(_dark
                    ? Color.FromArgb(_pressed == i ? 55 : 32, 255, 255, 255)
                    : Color.FromArgb(_pressed == i ? 36 : 20, 0, 0, 0));
                g.FillEllipse(hover, rect);
            }
            DrawButtonIcon(g, i, rect, fg);
        }

        var progressRect = ProgressRect();
        var progressY = progressRect.Top + progressRect.Height / 2;
        using (var track = new SolidBrush(_dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(25, 0, 0, 0)))
        using (var fill = new SolidBrush(Color.FromArgb(0, 210, 106)))
        {
            g.FillRectangle(track, progressRect.Left, progressY - 1.75f * d, progressRect.Width, 3.5f * d);
            var ratio = _dragging ? _dragRatio : ProgressRatio(now);
            var progressWidth = progressRect.Width * (float)ratio;
            if (progressWidth > 0)
                g.FillRectangle(fill, progressRect.Left, progressY - 1.75f * d, progressWidth, 3.5f * d);
            using var thumbShadow = new SolidBrush(Color.FromArgb(35, 0, 0, 0));
            using var thumb = new SolidBrush(Color.White);
            var thumbX = progressRect.Left + progressWidth;
            g.FillEllipse(thumbShadow, thumbX - 4 * d, progressY - 3 * d, 8 * d, 8 * d);
            g.FillEllipse(thumb, thumbX - 4 * d, progressY - 4 * d, 8 * d, 8 * d);
        }
    }

    private void DrawButtonIcon(Graphics g, int index, RectangleF rect, Color color)
    {
        var cx = rect.Left + rect.Width / 2;
        var cy = rect.Top + rect.Height / 2;
        var d = _dpi;
        using var pen = new Pen(color, 1.3f * d) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        if (index is 0 or 1)
        {
            DrawCentered(g, index == 0 ? "⚙" : "♫", _symbolFont!, brush, rect);
            return;
        }
        if (index == 3 && _snapshot.State == PlaybackState.Playing && !_snapshot.IsPaused)
        {
            g.FillRectangle(brush, cx - 5 * d, cy - 6 * d, 3 * d, 12 * d);
            g.FillRectangle(brush, cx + 2 * d, cy - 6 * d, 3 * d, 12 * d);
            return;
        }
        if (index == 3)
        {
            g.FillPolygon(brush, new[] { new PointF(cx - 4 * d, cy - 7 * d),
                new PointF(cx + 6 * d, cy), new PointF(cx - 4 * d, cy + 7 * d) });
            return;
        }
        var direction = index == 2 ? -1 : 1;
        var tip = cx + direction * 5 * d;
        var back = cx - direction * 4 * d;
        g.FillPolygon(brush, new[] { new PointF(back, cy - 6 * d),
            new PointF(tip, cy), new PointF(back, cy + 6 * d) });
        var barX = cx + direction * 6 * d;
        g.DrawLine(pen, barX, cy - 6 * d, barX, cy + 6 * d);
    }

    private static void DrawCentered(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, rect, format);
    }

    private RectangleF ButtonRect(int index)
    {
        var d = _dpi;
        var left = 14 * d;
        var top = 14 * d;
        return index switch
        {
            0 => new RectangleF(left + 280 * d - 14 * d - 26 * d, top + 6 * d, 26 * d, 26 * d),
            1 => new RectangleF(left + 14 * d, top + 58 * d, 30 * d, 30 * d),
            2 => new RectangleF(left + 280 * d - 14 * d - 112 * d, top + 56 * d, 32 * d, 32 * d),
            3 => new RectangleF(left + 280 * d - 14 * d - 73 * d, top + 54 * d, 36 * d, 36 * d),
            _ => new RectangleF(left + 280 * d - 14 * d - 32 * d, top + 56 * d, 32 * d, 32 * d)
        };
    }

    private RectangleF ProgressRect()
    {
        var d = _dpi;
        return new RectangleF(28 * d, 109 * d, 252 * d, 16 * d);
    }

    private int HitTest(int x, int y)
    {
        var point = new PointF(x, y);
        for (var i = 0; i < 5; i++)
            if (ButtonRect(i).Contains(point)) return i;
        if (ProgressRect().Contains(point)) return 5;
        return -1;
    }

    private double ProgressRatio(long now)
    {
        var duration = _snapshot.Duration.TotalMilliseconds;
        if (duration <= 0) return 0;
        var position = _snapshot.Position.TotalMilliseconds;
        // The coordinator refreshes snapshots; this small interpolation keeps the bar fluid.
        if (_snapshot.State == PlaybackState.Playing && !_snapshot.IsPaused)
            position += Math.Min(1000, Math.Max(0, now - _snapshotTick));
        return Math.Clamp(position / duration, 0, 1);
    }

    private void EnsureFonts(float dpi)
    {
        if (_titleFont is not null && Math.Abs(_fontDpi - dpi) < .01f) return;
        _titleFont?.Dispose(); _artistFont?.Dispose(); _symbolFont?.Dispose();
        _fontDpi = dpi;
        _titleFont = new Font("Microsoft YaHei UI", 13.5f * dpi, FontStyle.Bold, GraphicsUnit.Pixel);
        _artistFont = new Font("Microsoft YaHei UI", 11.5f * dpi, FontStyle.Regular, GraphicsUnit.Pixel);
        _symbolFont = new Font("Segoe UI Symbol", 18 * dpi, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private bool EnsureSurface()
    {
        if (_surface?.Width == _width && _surface.Height == _height) return true;
        DisposeSurface();
        _surfaceDc = CreateCompatibleDC(IntPtr.Zero);
        if (_surfaceDc == IntPtr.Zero) return false;
        var info = new BitmapInfo { Header = new BitmapInfoHeader
        {
            Size = 40, Width = _width, Height = -_height, Planes = 1, BitCount = 32
        } };
        _surfaceDib = CreateDIBSection(_surfaceDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (_surfaceDib == IntPtr.Zero) { DisposeSurface(); return false; }
        _surfaceOld = SelectObject(_surfaceDc, _surfaceDib);
        _surface = new Bitmap(_width, _height, _width * 4, PixelFormat.Format32bppPArgb, bits);
        return true;
    }

    private void DisposeSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_surfaceDc != IntPtr.Zero)
        {
            if (_surfaceOld != IntPtr.Zero) SelectObject(_surfaceDc, _surfaceOld);
            if (_surfaceDib != IntPtr.Zero) DeleteObject(_surfaceDib);
            DeleteDC(_surfaceDc);
        }
        _surfaceDc = _surfaceDib = _surfaceOld = IntPtr.Zero;
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try { return WindowProcCore(hwnd, message, wParam, lParam); }
        catch (Exception ex)
        {
            _log($"taskbar popup rendering failed: {ex}");
            return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private IntPtr WindowProcCore(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var x = (short)((long)lParam & 0xffff);
        var y = (short)(((long)lParam >> 16) & 0xffff);
        var now = Environment.TickCount64;
        switch (message)
        {
            case WmActivate:
                if (((long)wParam & 0xffff) != 0)
                    _everActivated = true;
                else if (_open)
                {
                    if (now - _openedAt < 280) _pendingDismiss = true;
                    else BeginClose();
                }
                return IntPtr.Zero;
            case WmMouseMove:
            {
                var hover = HitTest(x, y);
                if (_hover != hover) { _hover = hover; _dirty = true; }
                if (_dragging)
                {
                    var rect = ProgressRect();
                    _dragRatio = Math.Clamp((x - rect.Left) / rect.Width, 0, 1);
                    _dirty = true;
                }
                var tracking = new MouseTrackingInfo { Size = (uint)Marshal.SizeOf<MouseTrackingInfo>(), Flags = 2, Hwnd = hwnd };
                TrackMouseEvent(ref tracking);
                return IntPtr.Zero;
            }
            case WmMouseLeave:
                _hover = -1;
                _dirty = true;
                return IntPtr.Zero;
            case WmLButtonDown:
                _pressed = HitTest(x, y);
                if (_pressed >= 0)
                {
                    SetCapture(hwnd);
                    if (_pressed == 5 && _snapshot.Duration > TimeSpan.Zero)
                    {
                        _dragging = true;
                        var rect = ProgressRect();
                        _dragRatio = Math.Clamp((x - rect.Left) / rect.Width, 0, 1);
                    }
                    _dirty = true;
                }
                return IntPtr.Zero;
            case WmLButtonUp:
            {
                var pressed = _pressed;
                _pressed = -1;
                var dragging = _dragging;
                _dragging = false;
                if (GetCapture() == hwnd) ReleaseCapture();
                if (dragging)
                {
                    var rect = ProgressRect();
                    var ratio = Math.Clamp((x - rect.Left) / rect.Width, 0, 1);
                    _dirty = true;
                    if (_snapshot.Duration > TimeSpan.Zero)
                    {
                        var target = TimeSpan.FromMilliseconds(_snapshot.Duration.TotalMilliseconds * ratio);
                        _ui.TryEnqueue(() => _seek(target));
                    }
                }
                else if (pressed >= 0 && pressed == HitTest(x, y)) InvokeButton(pressed);
                _dirty = true;
                return IntPtr.Zero;
            }
            case WmCaptureChanged:
                if (_pressed >= 0 || _dragging) { _pressed = -1; _dragging = false; _dirty = true; }
                return IntPtr.Zero;
            case WmSetCursor:
                if (_hover >= 0)
                {
                    SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32649))); // IDC_HAND
                    return new IntPtr(1);
                }
                break;
            case WmKeyDown:
                if ((int)wParam == 0x1b) { BeginClose(); return IntPtr.Zero; }
                break;
            case WmClose:
                Hide();
                return IntPtr.Zero;
            case WmNcDestroy:
                _hwnd = IntPtr.Zero;
                break;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void InvokeButton(int index)
    {
        switch (index)
        {
            case 0: BeginClose(); _ui.TryEnqueue(() => _openSettings()); break;
            case 1: BeginClose(); _ui.TryEnqueue(() => _openApp()); break;
            case 2: _ui.TryEnqueue(() => _previous()); break;
            case 3: _ui.TryEnqueue(() => _togglePlay()); break;
            case 4: _ui.TryEnqueue(() => _next()); break;
        }
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        DisposeSurface();
        _titleFont?.Dispose(); _artistFont?.Dispose(); _symbolFont?.Dispose();
        UnregisterClass(_className, GetModuleHandle(null));
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public WndProc? WindowProc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative(int width, int height) { public int Width = width, Height = height; }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Red, Green, Blue; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseTrackingInfo { public uint Size, Flags; public IntPtr Hwnd; public uint HoverTime; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref MouseTrackingInfo tracking);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr resource);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref PointNative dst, ref SizeNative size, IntPtr srcDc, ref PointNative src, uint colorKey, ref BlendFunction blend, uint flags);
}
