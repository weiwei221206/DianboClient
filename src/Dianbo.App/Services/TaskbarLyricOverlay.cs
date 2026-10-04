using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLaborsImage = SixLabors.ImageSharp.Image;
using Dianbo.Core.Lyrics;
using Dianbo.Core.Models;
using Dianbo.Infrastructure.Storage;

namespace Dianbo.App.Services;

// A per-pixel-alpha Win32 window rendered by GDI+ directly into a reusable
// premultiplied 32-bit DIB; no XAML or WPF visual is hosted.
internal sealed class TaskbarLyricOverlay : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x80000;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopmost = 0x8;
    private const int GwlHwndParent = -8;
    private const uint SwpNoActivate = 0x10;
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoSize = 0x1;
    private const uint SwpShowWindow = 0x40;
    private const uint SwpHideWindow = 0x80;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcDestroy = 0x0082;
    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;
    private const int WmQuit = 0x0012;
    private const int WmTimer = 0x0113;
    private const int WmApply = 0x8001;
    private const uint TtmAddToolW = 0x0400 + 50;
    private const uint TtmUpdateTipTextW = 0x0400 + 57;
    private const uint TtmTrackActivate = 0x0400 + 17;
    private const uint TtmTrackPosition = 0x0400 + 18;
    private const uint TtmGetBubbleSize = 0x0400 + 30;
    private const uint TtmUpdate = 0x0400 + 29;
    private const uint TtfTrack = 0x0020;
    private const uint TtfAbsolute = 0x0080;

    private readonly WndProc _wndProc;
    private readonly string _className = "DianboTaskbarLyric_" + Guid.NewGuid().ToString("N");
    private readonly Thread _renderThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly Action _openSettings;
    private readonly Action _openApp;
    private readonly Action _previous;
    private readonly Action _togglePlay;
    private readonly Action _next;
    private readonly Action<TimeSpan> _seek;
    private readonly Action<int> _setVolume;
    private readonly Action<string> _log;
    private readonly HttpClient _coverClient = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;
    private IntPtr _hwnd;
    private TaskbarLyricNativePopup? _controlPopup;
    private Exception? _startupError;
    private IntPtr _taskbar;
    private TaskbarLyricSettings _settings;
    private Song? _song;
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Initial;
    private LyricDocument _lyrics = LyricDocument.Empty;
    private int _lyricIndex = int.MinValue;
    private string _main = "点波音乐 - 任务栏歌词已就绪";
    private string _sub = "";
    private long _transitionStart;
    private Bitmap? _cover;
    private string? _coverUrl;
    private int _coverRequest;
    private float _angle;
    private int _volumeHintValue;
    private int _volumeHintScreenX;
    private int _volumeHintScreenY;
    private long _volumeHintUntil;
    private IntPtr _volumeTooltip;
    private IntPtr _volumeTooltipText;
    private bool _volumeTooltipActive;
    private bool _visible;
    private volatile bool _disposed;
    private int _lastX = int.MinValue, _lastY, _lastW, _lastH;
    private long _lastFrameTimestamp;
    private long _lastFollowTime;
    private bool _dirty = true;
    private bool _taskbarDark;
    private bool _cardDark;
    private readonly Func<bool>? _isCardDark;
    private bool _highResolutionTimer;
    private IntPtr _frameTimer;
    private IntPtr _surfaceDc;
    private IntPtr _surfaceDib;
    private IntPtr _surfaceOld;
    private Bitmap? _surfaceBitmap;
    private Font? _mainFont;
    private Font? _subFont;
    private float _fontDpi;
    private TextRaster? _mainRaster;
    private TextRaster? _subRaster;

    public TaskbarLyricOverlay(TaskbarLyricSettings settings, Microsoft.UI.Dispatching.DispatcherQueue ui,
        Action openSettings, Action openApp, Action previous, Action togglePlay,
        Action next, Action<TimeSpan> seek,
        Action<int> setVolume, Action<string> log,
        Func<bool>? isCardDark = null)
    {
        _settings = settings.Copy();
        _ui = ui;
        _openSettings = openSettings;
        _openApp = openApp;
        _previous = previous;
        _togglePlay = togglePlay;
        _next = next;
        _seek = seek;
        _setVolume = setVolume;
        _log = log;
        _isCardDark = isCardDark;
        _taskbarDark = IsTaskbarDark();
        _cardDark = ResolveCardDark();
        ThemeHelper.ThemeChanged += OnThemeChanged;
        _wndProc = WindowProc;
        _renderThread = new Thread(RunWindow) { IsBackground = true, Name = "Dianbo taskbar lyrics" };
        _renderThread.Start();
        _ready.Wait();
        if (_startupError is not null)
            throw new InvalidOperationException("Could not start taskbar lyric window.", _startupError);
    }

    private void RunWindow()
    {
        try
        {
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = _wndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = _className
        };
        if (RegisterClassEx(ref wc) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _hwnd = CreateWindowEx(WsExLayered | WsExToolWindow | WsExNoActivate | WsExTopmost,
            _className, "点波音乐任务栏歌词", WsPopup, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        InitializeVolumeTooltip();
        _frameTimer = CreateWaitableTimerEx(IntPtr.Zero, null, 0x2, 0x00100002);
        if (_frameTimer == IntPtr.Zero)
            _frameTimer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x00100002);
        long firstFrame = -160_000; // 16 ms in 100 ns units (~60 FPS)
        if (_frameTimer != IntPtr.Zero && !SetWaitableTimerEx(_frameTimer, ref firstFrame, 16,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
        {
            CloseHandle(_frameTimer);
            _frameTimer = IntPtr.Zero;
        }
        if (_frameTimer == IntPtr.Zero && SetTimer(_hwnd, new UIntPtr(1), 16, IntPtr.Zero) == UIntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        UpdateTimerResolution();
        Tick();
        Render();
        _ready.Set();
        if (_frameTimer != IntPtr.Zero)
        {
            while (true)
            {
                var wait = MsgWaitForMultipleObjectsEx(1, ref _frameTimer, uint.MaxValue, 0x04FF, 0x0004);
                if (wait is not (0 or 1))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var processed = 0;
                while (processed++ < 64 && PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
                {
                    if (message.message == WmQuit) return;
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
                if (wait == 0) FrameTick();
            }
        }
        else
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            _log($"taskbar lyric window failed: {ex}");
        }
        finally
        {
            _controlPopup?.Dispose();
            DisposeVolumeTooltip();
            if (_highResolutionTimer) timeEndPeriod(1);
            if (_frameTimer != IntPtr.Zero) CloseHandle(_frameTimer);
            if (_hwnd != IntPtr.Zero)
            {
                KillTimer(_hwnd, new UIntPtr(1));
                DestroyWindow(_hwnd);
            }
            DisposeFonts();
            DisposeSurface();
            _cover?.Dispose();
            UnregisterClass(_className, GetModuleHandle(null));
        }
    }

    public void ApplySettings(TaskbarLyricSettings settings)
    {
        var copy = settings.Copy();
        Post(() => ApplySettingsCore(copy));
    }

    private void ApplySettingsCore(TaskbarLyricSettings settings)
    {
        _settings = settings;
        UpdateTimerResolution();
        DisposeFonts();
        _lyricIndex = int.MinValue;
        UpdateLine();
        _lastX = int.MinValue;
        Tick();
        Render();
    }

    public void Update(PlaybackSnapshot snapshot, Song? song, LyricDocument lyrics)
    {
        Post(() => UpdateCore(snapshot, song, lyrics));
    }

    private void UpdateCore(PlaybackSnapshot snapshot, Song? song, LyricDocument lyrics)
    {
        _snapshot = snapshot;
        if (!ReferenceEquals(_lyrics, lyrics)) _lyricIndex = int.MinValue;
        _lyrics = lyrics;
        if (_song?.Id != song?.Id || !string.Equals(_coverUrl, song?.CoverUrl, StringComparison.Ordinal))
        {
            _song = song;
            _coverUrl = song?.CoverUrl;
            _cover?.Dispose();
            _cover = null;
            _angle = 0;
            _lyricIndex = int.MinValue;
            _ = LoadCoverAsync(_coverUrl, ++_coverRequest);
        }
        UpdateLine();
        _dirty = true;
    }

    private bool Post(Action action)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return false;
        _pending.Enqueue(action);
        return PostMessage(_hwnd, WmApply, IntPtr.Zero, IntPtr.Zero);
    }

    private async Task LoadCoverAsync(string? url, int request)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try
        {
            var bytes = await _coverClient.GetByteArrayAsync(uri);
            var bitmap = DecodeCover(bytes);
            if (!Post(() =>
            {
                if (_disposed || request != _coverRequest) { bitmap.Dispose(); return; }
                _cover?.Dispose();
                _cover = bitmap;
                _dirty = true;
            })) bitmap.Dispose();
        }
        catch (Exception ex) { _log($"taskbar cover decode failed: {ex.GetType().Name}"); }
    }

    private static Bitmap DecodeCover(byte[] bytes)
    {
        // ImageSharp ships WebP/JPEG/PNG decoders with the app, including on LTSC.
        using var image = SixLaborsImage.Load<Rgba32>(bytes);
        if (image.Width > 128 || image.Height > 128)
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(128, 128),
                Mode = ResizeMode.Max
            }));
        var width = image.Width;
        var height = image.Height;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                image.ProcessPixelRows(accessor =>
                {
                    var row = new byte[width * 4];
                    for (var y = 0; y < height; y++)
                    {
                        var pixels = accessor.GetRowSpan(y);
                        for (var x = 0; x < width; x++)
                        {
                            var pixel = pixels[x];
                            var alpha = pixel.A;
                            var offset = x * 4;
                            row[offset] = (byte)(pixel.B * alpha / 255);
                            row[offset + 1] = (byte)(pixel.G * alpha / 255);
                            row[offset + 2] = (byte)(pixel.R * alpha / 255);
                            row[offset + 3] = alpha;
                        }
                        Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, y * locked.Stride), row.Length);
                    }
                });
            }
            finally { bitmap.UnlockBits(locked); }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        return bitmap;
    }

    private void UpdateLine()
    {
        var index = _lyrics.IndexAt(_snapshot.Position);
        if (index == _lyricIndex) return;
        _lyricIndex = index;
        LyricGroup? group = index >= 0 ? _lyrics.Groups[index] : null;
        if (group is not null && string.IsNullOrWhiteSpace(group.OriginalText))
        {
            var hasLaterLyric = false;
            for (var candidate = index + 1; candidate < _lyrics.Groups.Count; candidate++)
            {
                if (string.IsNullOrWhiteSpace(_lyrics.Groups[candidate].OriginalText)) continue;
                hasLaterLyric = true;
                break;
            }
            if (!hasLaterLyric)
            {
                for (var candidate = index - 1; candidate >= 0; candidate--)
                {
                    if (string.IsNullOrWhiteSpace(_lyrics.Groups[candidate].OriginalText)) continue;
                    group = _lyrics.Groups[candidate];
                    break;
                }
            }
        }
        var main = string.IsNullOrWhiteSpace(group?.OriginalText) ? _song?.Name ?? "点波音乐 - 任务栏歌词已就绪" : group.OriginalText;
        var sub = _settings.ShowTranslation && !string.IsNullOrWhiteSpace(group?.TranslationText)
            ? group.TranslationText
            : group is null && _song is not null ? _song.Artist : string.Empty;
        if (main != _main || sub != _sub)
        {
            _main = main;
            _sub = sub;
            _transitionStart = Environment.TickCount64;
        }
    }

    private void FrameTick()
    {
        if (_disposed) return;
        var now = Environment.TickCount64;
        var frameTimestamp = Stopwatch.GetTimestamp();
        // A delayed frame should not make the small cover jump several degrees at once.
        var deltaSeconds = _lastFrameTimestamp == 0 ? 0f
            : (float)Math.Clamp((frameTimestamp - _lastFrameTimestamp) / (double)Stopwatch.Frequency, 0, .05);
        _lastFrameTimestamp = frameTimestamp;
        if (now - _lastFollowTime >= 200 || _lastX == int.MinValue)
        {
            _lastFollowTime = now;
            Tick();
        }
        if (_volumeHintUntil != 0 && now >= _volumeHintUntil)
        {
            _volumeHintUntil = 0;
            HideVolumeTooltip();
        }
        if (!_visible) return;
        _controlPopup?.Update(_song, _snapshot, _cover, _cardDark);
        _controlPopup?.FrameTick(now);
        var rotating = _settings.ShowCover && _settings.RotateCover && _song is not null
            && !_snapshot.IsPaused && _snapshot.State == PlaybackState.Playing;
        if (rotating)
        {
            _angle = (_angle + 40f * deltaSeconds) % 360f;
            _dirty = true;
        }
        if (_settings.AnimationType != "None"
            && now - _transitionStart < Math.Clamp(_settings.AnimationDurationMs, 100, 600)) _dirty = true;
        if (_dirty) Render();
    }

    private void UpdateTimerResolution()
    {
        if (_settings.Enabled && !_highResolutionTimer)
            _highResolutionTimer = timeBeginPeriod(1) == 0;
        else if (!_settings.Enabled && _highResolutionTimer)
        {
            timeEndPeriod(1);
            _highResolutionTimer = false;
        }
    }

    private void Tick()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var rect))
        {
            Hide();
            return;
        }
        if (_taskbar != taskbar)
        {
            _taskbar = taskbar;
            SetWindowLongPtr(_hwnd, GwlHwndParent, taskbar);
            _lastX = int.MinValue;
        }
        var monitor = MonitorFromWindow(taskbar, 1);
        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        var monitorValid = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref mi);
        var visibleThickness = monitorValid
            ? rect.Width >= rect.Height
                ? Math.Min(rect.Bottom, mi.rcMonitor.Bottom) - Math.Max(rect.Top, mi.rcMonitor.Top)
                : Math.Min(rect.Right, mi.rcMonitor.Right) - Math.Max(rect.Left, mi.rcMonitor.Left)
            : rect.Height;
        if (!_settings.Enabled || (_settings.AutoHideWithTaskbar && visibleThickness <= 8)
            || (_settings.HideWhenFullscreen && monitorValid && IsFullscreen(mi.rcMonitor, taskbar)))
        {
            Hide();
            return;
        }
        // Show Desktop can hide or minimize an owned topmost window without changing our state.
        if (_visible && (!IsWindowVisible(_hwnd) || IsIconic(_hwnd)))
        {
            if (IsIconic(_hwnd)) ShowWindow(_hwnd, 9); // SW_RESTORE
            _visible = false;
            _lastX = int.MinValue;
        }
        var dpi = GetDpiForWindow(taskbar) / 96f;
        if (dpi <= 0) dpi = 1;
        var isHorizontal = rect.Width >= rect.Height;
        int width, height, x, y;
        if (isHorizontal)
        {
            width = Math.Max(160, (int)(_settings.Width * dpi));
            height = Math.Max(32, rect.Height);
            x = _settings.PositionMode switch
            {
                "center" => rect.Left + (rect.Width - width) / 2 + (int)(_settings.XOffset * dpi),
                "weather_right" => rect.Left + (int)((200 + _settings.XOffset) * dpi),
                _ => rect.Left + (int)(_settings.XOffset * dpi)
            };
            x = Math.Clamp(x, rect.Left, Math.Max(rect.Left, rect.Right - width));
            y = rect.Top + (int)(_settings.YOffset * dpi);
        }
        else
        {
            width = Math.Max(120, rect.Width);
            height = Math.Clamp((int)(40 * dpi), 32, rect.Height);
            x = rect.Left;
            y = rect.Top + (int)(_settings.YOffset * dpi);
            y = Math.Clamp(y, rect.Top, Math.Max(rect.Top, rect.Bottom - height));
        }
        if (!_visible || x != _lastX || y != _lastY || width != _lastW || height != _lastH)
        {
            _lastX = x; _lastY = y; _lastW = width; _lastH = height;
            SetWindowPos(_hwnd, new IntPtr(-1), x, y, width, height, SwpNoActivate | SwpShowWindow);
            _visible = true;
            _dirty = true;
        }
        EnsureAboveTaskbar(taskbar);
        var taskbarDark = IsTaskbarDark();
        if (_taskbarDark != taskbarDark) { _taskbarDark = taskbarDark; _dirty = true; }
        var cardDark = ResolveCardDark();
        if (_cardDark != cardDark)
        {
            _cardDark = cardDark;
            _controlPopup?.Update(_song, _snapshot, _cover, _cardDark);
        }
    }

    private void EnsureAboveTaskbar(IntPtr taskbar)
    {
        // Hovering taskbar items can raise its host above an already-topmost lyric window.
        var current = GetWindow(_hwnd, 3); // GW_HWNDPREV
        for (var depth = 0; current != IntPtr.Zero && depth < 100; depth++)
        {
            var root = GetAncestor(current, 2); // GA_ROOT
            if (current == taskbar || root == taskbar || IsTaskbarHost(current))
            {
                var flags = SwpNoMove | SwpNoSize | SwpNoActivate;
                SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, flags);
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, flags);
                if (_volumeTooltipActive && _volumeTooltip != IntPtr.Zero)
                    SetWindowPos(_volumeTooltip, new IntPtr(-1), 0, 0, 0, 0, flags);
                _dirty = true;
                return;
            }
            current = GetWindow(current, 3);
        }
    }

    private static bool IsTaskbarHost(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "TrayWindow" or "XamlExplorerHostIslandWindow";
    }

    private static bool IsFullscreen(Rect monitorRect, IntPtr taskbar)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == taskbar || !IsWindowVisible(foreground) || IsIconic(foreground)) return false;
        var className = new StringBuilder(128);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow")
            return false;
        if (GetWindowRect(foreground, out var bounds))
            return bounds.Left <= monitorRect.Left + 2 && bounds.Top <= monitorRect.Top + 2
                && bounds.Right >= monitorRect.Right - 2 && bounds.Bottom >= monitorRect.Bottom - 2;
        return false;
    }

    private void Hide()
    {
        HideVolumeTooltip();
        if (!_visible) return;
        _controlPopup?.Hide();
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x1 | 0x2 | SwpNoActivate | SwpHideWindow);
        _visible = false;
    }

    private void Render()
    {
        if (!_visible || _disposed || _lastW <= 0 || _lastH <= 0) return;
        if (!EnsureSurface()) return;
        var dpi = _taskbar == IntPtr.Zero ? 1f : Math.Max(1f, GetDpiForWindow(_taskbar) / 96f);
        using (var g = Graphics.FromImage(_surfaceBitmap!))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);
            DrawLyricBar(g, dpi);
        }
        Present();
        _dirty = false;
    }

    private void DrawLyricBar(Graphics g, float dpi)
    {
        const int top = 0;
        var barHeight = _lastH;
        // Alpha 1 keeps the whole lyric strip clickable while remaining visually transparent.
        using (var hitArea = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
            g.FillRectangle(hitArea, new RectangleF(0, top, _lastW, barHeight));
        if (_settings.ShowBackgroundCard)
        {
            using var mask = new SolidBrush(Color.FromArgb(75, 20, 20, 20));
            using var path = Rounded(new RectangleF(0, top + 2 * dpi,
                _lastW, barHeight - 4 * dpi), 6 * dpi);
            g.FillPath(mask, path);
        }
        var coverSize = _settings.ShowCover ? Math.Min(barHeight - 6 * dpi, _settings.CoverSize * dpi) : 0;
        var left = 6 * dpi;
        if (coverSize > 0)
        {
            var cy = top + (barHeight - coverSize) / 2;
            var coverRect = new RectangleF(left, cy, coverSize, coverSize);
            using var clip = new GraphicsPath();
            clip.AddEllipse(coverRect);
            var state = g.Save();
            g.SetClip(clip);
            if (_cover is not null)
            {
                g.TranslateTransform(left + coverSize / 2, cy + coverSize / 2);
                g.RotateTransform(_angle);
                g.DrawImage(_cover, -coverSize / 2, -coverSize / 2, coverSize, coverSize);
            }
            else
            {
                using var placeholder = new SolidBrush(Color.FromArgb(230, 70, 70, 70));
                g.FillEllipse(placeholder, coverRect);
                using var dot = new SolidBrush(Color.FromArgb(220, 190, 190, 190));
                g.FillEllipse(dot, left + coverSize * .42f, cy + coverSize * .42f, coverSize * .16f, coverSize * .16f);
            }
            g.Restore(state);
            using (var outline = new Pen(Color.FromArgb(50, 255, 255, 255), dpi))
                g.DrawEllipse(outline, coverRect);
            left += coverSize + 8 * dpi;
        }
        var dark = _taskbarDark;
        var mainColor = _settings.ColorMode switch
        {
            "white" => Color.White,
            "black" => Color.FromArgb(30, 30, 30),
            "custom" when TaskbarLyricAppearance.TryParseRgb(_settings.CustomColor, out var custom) => custom,
            _ => dark ? Color.White : Color.FromArgb(30, 30, 30)
        };
        var subColor = _settings.ColorMode switch
        {
            "white" => Color.FromArgb(200, 255, 255, 255),
            "black" => Color.FromArgb(200, 70, 70, 70),
            "custom" => Color.FromArgb((int)Math.Round(mainColor.A * .78), mainColor.R, mainColor.G, mainColor.B),
            _ => dark ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(200, 70, 70, 70)
        };
        EnsureFonts(dpi);
        var mainFont = _mainFont!;
        var subFont = _subFont!;
        var textWidth = Math.Max(10, (int)Math.Ceiling(_lastW - left - 7 * dpi));
        var duration = Math.Clamp(_settings.AnimationDurationMs, 100, 600);
        var progress = _settings.AnimationType == "None"
            ? 1f : Math.Clamp((Environment.TickCount64 - _transitionStart) / (float)duration, 0f, 1f);
        var eased = 1f - MathF.Pow(1f - progress, 3f);
        var (opacity, offsetX, offsetY) = _settings.AnimationType switch
        {
            "SlideFade" => (.2f + .8f * eased, 0f, 7f * (1f - eased) * dpi),
            "FadeOnly" => (progress, 0f, 0f),
            "None" => (1f, 0f, 0f),
            _ => (.2f + .8f * eased, -18f * (1f - eased) * dpi, 0f)
        };
        DrawLines(_main, _sub, opacity, offsetX, offsetY);

        void DrawLines(string main, string sub, float opacity, float offsetX, float offsetY)
        {
            var mainRaster = GetTextRaster(ref _mainRaster, main, mainFont, mainColor, textWidth, dpi);
            if (string.IsNullOrWhiteSpace(sub))
            {
                DrawTextRaster(g, mainRaster.Bitmap, left + offsetX, top + (barHeight - mainFont.Height) / 2f + offsetY, opacity);
            }
            else
            {
                var lineHeight = mainFont.Height + subFont.Height;
                var y = top + (barHeight - lineHeight) / 2f;
                var subRaster = GetTextRaster(ref _subRaster, sub, subFont, subColor, textWidth, dpi);
                DrawTextRaster(g, mainRaster.Bitmap, left + offsetX, y + offsetY, opacity);
                DrawTextRaster(g, subRaster.Bitmap, left + offsetX, y + mainFont.Height + offsetY, opacity);
            }
        }
    }

    private TextRaster GetTextRaster(ref TextRaster? cache, string text, Font font, Color color, int width, float dpi)
    {
        var spacing = (float)Math.Clamp(_settings.CharacterSpacing, -3, 1) * dpi;
        if (cache is not null && cache.Text == text && ReferenceEquals(cache.Font, font)
            && cache.ColorArgb == color.ToArgb() && cache.Width == width && Math.Abs(cache.Spacing - spacing) < .01f)
            return cache;

        cache?.Dispose();
        var bitmap = new Bitmap(width, font.Height + 4, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            var elements = new List<(string Text, float Width)>();
            var enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                var element = enumerator.GetTextElement();
                var measured = g.MeasureString(element, font, new SizeF(1000, font.Height + 4), fmt).Width;
                elements.Add((element, measured));
            }
            var naturalWidth = 0f;
            var next = 0f;
            foreach (var element in elements)
            {
                naturalWidth = next + element.Width;
                next += Math.Max(1, element.Width + spacing);
            }
            var clipped = naturalWidth > width;
            var ellipsisWidth = clipped ? g.MeasureString("…", font, new SizeF(1000, font.Height + 4), fmt).Width : 0;
            var limit = clipped ? width - ellipsisWidth : width;
            var x = 0f;
            foreach (var element in elements)
            {
                if (x + element.Width > limit) break;
                g.DrawString(element.Text, font, brush, new PointF(x, 0), fmt);
                x += Math.Max(1, element.Width + spacing);
            }
            if (clipped)
                g.DrawString("…", font, brush, new PointF(Math.Max(0, Math.Min(x, width - ellipsisWidth)), 0), fmt);
        }
        cache = new TextRaster(text, font, color.ToArgb(), width, spacing, bitmap);
        return cache;
    }

    private static void DrawTextRaster(Graphics g, Bitmap bitmap, float x, float y, float opacity)
    {
        var target = new Rectangle((int)Math.Round(x), (int)Math.Round(y), bitmap.Width, bitmap.Height);
        if (opacity >= .999f)
        {
            g.DrawImageUnscaled(bitmap, target.Location);
            return;
        }
        using var attributes = new ImageAttributes();
        var matrix = new ColorMatrix
        {
            Matrix00 = 1, Matrix11 = 1, Matrix22 = 1,
            Matrix33 = Math.Clamp(opacity, 0, 1), Matrix44 = 1
        };
        attributes.SetColorMatrix(matrix);
        g.DrawImage(bitmap, target, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
    }

    private sealed class TextRaster(string text, Font font, int colorArgb, int width, float spacing, Bitmap bitmap) : IDisposable
    {
        public string Text { get; } = text;
        public Font Font { get; } = font;
        public int ColorArgb { get; } = colorArgb;
        public int Width { get; } = width;
        public float Spacing { get; } = spacing;
        public Bitmap Bitmap { get; } = bitmap;
        public void Dispose() => Bitmap.Dispose();
    }

    private Font NewFont(float size, FontStyle style)
    {
        var family = TaskbarLyricAppearance.IsInstalledFont(_settings.FontFamily)
            ? _settings.FontFamily : TaskbarLyricAppearance.DefaultFontFamily;
        try
        {
            return TaskbarLyricAppearance.IsInstalledFont(family)
                ? new Font(family, Math.Max(8, size), style, GraphicsUnit.Pixel)
                : new Font(FontFamily.GenericSansSerif, Math.Max(8, size), style, GraphicsUnit.Pixel);
        }
        catch (ArgumentException)
        {
            return new Font(FontFamily.GenericSansSerif, Math.Max(8, size), style, GraphicsUnit.Pixel);
        }
    }

    private void EnsureFonts(float dpi)
    {
        if (_mainFont is not null && _subFont is not null && Math.Abs(_fontDpi - dpi) < .01f) return;
        DisposeFonts();
        _fontDpi = dpi;
        _mainFont = NewFont((float)_settings.MainFontSize * dpi, FontStyle.Bold);
        _subFont = NewFont((float)_settings.SubFontSize * dpi, FontStyle.Regular);
    }

    private void DisposeFonts()
    {
        _mainRaster?.Dispose();
        _subRaster?.Dispose();
        _mainRaster = _subRaster = null;
        _mainFont?.Dispose();
        _subFont?.Dispose();
        _mainFont = null;
        _subFont = null;
    }

    private void InitializeVolumeTooltip()
    {
        var controls = new InitCommonControls { Size = (uint)Marshal.SizeOf<InitCommonControls>(), Classes = 0x0002 };
        if (!InitCommonControlsEx(ref controls))
        {
            _log($"taskbar volume tooltip initialization failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        _volumeTooltip = CreateWindowEx(WsExTopmost | WsExToolWindow | WsExNoActivate,
            "tooltips_class32", string.Empty, WsPopup | 0x0001 | 0x0002,
            0, 0, 0, 0, _hwnd, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_volumeTooltip == IntPtr.Zero)
        {
            _log($"taskbar volume tooltip creation failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        _volumeTooltipText = Marshal.AllocHGlobal(64 * sizeof(char));
        WriteVolumeTooltipText();
        var info = VolumeToolInfo();
        if (SendTooltipMessage(_volumeTooltip, TtmAddToolW, IntPtr.Zero, ref info) == IntPtr.Zero)
        {
            _log("taskbar volume tooltip registration failed");
            DestroyWindow(_volumeTooltip);
            _volumeTooltip = IntPtr.Zero;
            Marshal.FreeHGlobal(_volumeTooltipText);
            _volumeTooltipText = IntPtr.Zero;
        }
    }

    private ToolInfo VolumeToolInfo() => new()
    {
        Size = (uint)Marshal.SizeOf<ToolInfo>(),
        Flags = TtfTrack | TtfAbsolute,
        Window = _hwnd,
        Id = new UIntPtr(1),
        Text = _volumeTooltipText
    };

    private void WriteVolumeTooltipText()
    {
        var text = $"音量: {_volumeHintValue}%";
        for (var i = 0; i < text.Length; i++)
            Marshal.WriteInt16(_volumeTooltipText, i * sizeof(char), (short)text[i]);
        Marshal.WriteInt16(_volumeTooltipText, text.Length * sizeof(char), 0);
    }

    private void ShowVolumeTooltip()
    {
        if (_volumeTooltip == IntPtr.Zero || _taskbar == IntPtr.Zero
            || !GetWindowRect(_taskbar, out var taskbarRect)) return;
        WriteVolumeTooltipText();
        var info = VolumeToolInfo();
        SendTooltipMessage(_volumeTooltip, TtmUpdateTipTextW, IntPtr.Zero, ref info);
        var bubble = SendTooltipMessage(_volumeTooltip, TtmGetBubbleSize, IntPtr.Zero, ref info);
        var packedSize = unchecked((uint)bubble.ToInt64());
        var width = (int)(packedSize & 0xffff);
        var height = (int)(packedSize >> 16);
        if (width <= 0 || height <= 0) { width = 110; height = 32; }

        var monitor = MonitorFromWindow(_taskbar, 1);
        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        var screen = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo)
            ? monitorInfo.rcMonitor : taskbarRect;
        var horizontal = taskbarRect.Width >= taskbarRect.Height;
        var x = horizontal ? _volumeHintScreenX - width / 2
            : taskbarRect.Left - screen.Left < screen.Right - taskbarRect.Right
                ? taskbarRect.Right + 10 : taskbarRect.Left - width - 10;
        var y = horizontal
            ? taskbarRect.Top - screen.Top < screen.Bottom - taskbarRect.Bottom
                ? taskbarRect.Bottom + 10 : taskbarRect.Top - height - 10
            : _volumeHintScreenY - height / 2;
        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - width));
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        var position = new IntPtr(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
        SendTooltipMessage(_volumeTooltip, TtmTrackPosition, IntPtr.Zero, position);
        if (!_volumeTooltipActive)
        {
            SendTooltipMessage(_volumeTooltip, TtmTrackActivate, new IntPtr(1), ref info);
            _volumeTooltipActive = true;
        }
        SendTooltipMessage(_volumeTooltip, TtmTrackPosition, IntPtr.Zero, position);
        SendTooltipMessage(_volumeTooltip, TtmUpdate, IntPtr.Zero, IntPtr.Zero);
    }

    private void HideVolumeTooltip()
    {
        if (!_volumeTooltipActive || _volumeTooltip == IntPtr.Zero) return;
        var info = VolumeToolInfo();
        SendTooltipMessage(_volumeTooltip, TtmTrackActivate, IntPtr.Zero, ref info);
        _volumeTooltipActive = false;
    }

    private void DisposeVolumeTooltip()
    {
        HideVolumeTooltip();
        if (_volumeTooltip != IntPtr.Zero)
        {
            DestroyWindow(_volumeTooltip);
            _volumeTooltip = IntPtr.Zero;
        }
        if (_volumeTooltipText != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_volumeTooltipText);
            _volumeTooltipText = IntPtr.Zero;
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private bool EnsureSurface()
    {
        if (_surfaceBitmap?.Width == _lastW && _surfaceBitmap.Height == _lastH) return true;
        DisposeSurface();
        _surfaceDc = CreateCompatibleDC(IntPtr.Zero);
        if (_surfaceDc == IntPtr.Zero) return false;
        var bmi = new BitmapInfo { Header = new BitmapInfoHeader { Size = 40, Width = _lastW, Height = -_lastH, Planes = 1, BitCount = 32, Compression = 0 } };
        _surfaceDib = CreateDIBSection(_surfaceDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (_surfaceDib == IntPtr.Zero)
        {
            DisposeSurface();
            return false;
        }
        _surfaceOld = SelectObject(_surfaceDc, _surfaceDib);
        _surfaceBitmap = new Bitmap(_lastW, _lastH, _lastW * 4, PixelFormat.Format32bppPArgb, bits);
        return true;
    }

    private void Present()
    {
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return;
        var target = new PointNative(_lastX, _lastY);
        var size = new SizeNative(_lastW, _lastH);
        var source = new PointNative(0, 0);
        var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(_hwnd, screen, ref target, ref size, _surfaceDc, ref source, 0, ref blend, 2);
        ReleaseDC(IntPtr.Zero, screen);
    }

    private void DisposeSurface()
    {
        _surfaceBitmap?.Dispose();
        _surfaceBitmap = null;
        if (_surfaceDc != IntPtr.Zero)
        {
            if (_surfaceOld != IntPtr.Zero) SelectObject(_surfaceDc, _surfaceOld);
            if (_surfaceDib != IntPtr.Zero) DeleteObject(_surfaceDib);
            DeleteDC(_surfaceDc);
        }
        _surfaceDc = _surfaceDib = _surfaceOld = IntPtr.Zero;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try { return WindowProcCore(hwnd, message, wParam, lParam); }
        catch (Exception ex)
        {
            _log($"taskbar lyric rendering failed: {ex}");
            return IntPtr.Zero;
        }
    }

    private IntPtr WindowProcCore(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmApply)
        {
            while (_pending.TryDequeue(out var action)) action();
            return IntPtr.Zero;
        }
        if (message == WmTimer)
        {
            FrameTick();
            return IntPtr.Zero;
        }
        if (message == WmClose)
        {
            _controlPopup?.Dispose();
            _controlPopup = null;
            DisposeVolumeTooltip();
            DestroyWindow(hwnd);
            return IntPtr.Zero;
        }
        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        if (message == WmMouseActivate) return new IntPtr(3);
        if (message == WmLButtonUp || message == WmRButtonUp)
        {
            if (message == WmRButtonUp)
            {
                _controlPopup?.Hide();
                _ui.TryEnqueue(() => _openSettings());
            }
            else if (_taskbar != IntPtr.Zero && GetWindowRect(_taskbar, out var taskbarRect))
            {
                var monitor = MonitorFromWindow(_taskbar, 1);
                var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                var screen = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
                    ? info.rcMonitor : taskbarRect;
                var dpi = Math.Max(1f, GetDpiForWindow(_taskbar) / 96f);
                var anchor = new TaskbarLyricPopupAnchor(
                    _lastX + (int)(6 * dpi), taskbarRect.Top, taskbarRect.Bottom,
                    screen.Left, screen.Top, screen.Right, screen.Bottom, dpi);
                try
                {
                    _cardDark = ResolveCardDark();
                    _controlPopup ??= new TaskbarLyricNativePopup(_hwnd, _ui,
                        _openSettings, _openApp, _previous, _togglePlay, _next, _seek, _log);
                    _controlPopup.Update(_song, _snapshot, _cover, _cardDark);
                    _controlPopup.Toggle(anchor, _cardDark);
                }
                catch (Exception ex) { _log($"taskbar popup failed: {ex}"); }
            }
            return IntPtr.Zero;
        }
        if (message == WmMouseWheel)
        {
            var delta = (short)(((long)wParam >> 16) & 0xffff);
            if (delta == 0) return IntPtr.Zero;
            var now = Environment.TickCount64;
            var current = now < _volumeHintUntil ? _volumeHintValue : _snapshot.Volume;
            var steps = Math.Max(1, Math.Abs((int)delta) / 120);
            var volume = Math.Clamp(current + Math.Sign(delta) * steps * 5, 0, 100);
            _volumeHintValue = volume;
            _volumeHintScreenX = (short)((long)lParam & 0xffff);
            _volumeHintScreenY = (short)(((long)lParam >> 16) & 0xffff);
            _volumeHintUntil = now + 1400;
            _ui.TryEnqueue(() => _setVolume(volume));
            ShowVolumeTooltip();
            return IntPtr.Zero;
        }
        if (message == WmNcDestroy) _hwnd = IntPtr.Zero;
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void OnThemeChanged() => NotifyThemeChanged();

    public void NotifyThemeChanged()
    {
        Post(() =>
        {
            var taskbarDark = IsTaskbarDark();
            if (_taskbarDark != taskbarDark) { _taskbarDark = taskbarDark; _dirty = true; }
            var cardDark = ResolveCardDark();
            if (_cardDark != cardDark)
            {
                _cardDark = cardDark;
                _controlPopup?.Update(_song, _snapshot, _cover, _cardDark);
            }
        });
    }

    private bool ResolveCardDark()
    {
        try
        {
            if (_isCardDark is not null) return _isCardDark();
            return ThemeHelper.IsDark;
        }
        catch (Exception ex)
        {
            _log($"resolve card dark failed: {ex.Message}");
            return ThemeHelper.IsSystemAppDark();
        }
    }

    private static bool IsTaskbarDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("SystemUsesLightTheme") ?? 1) == 0;
        }
        catch (Exception) { return true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemeHelper.ThemeChanged -= OnThemeChanged;
        ++_coverRequest;
        _coverClient.Dispose();
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _renderThread) _renderThread.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx { public uint cbSize, style; public WndProc? lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName, lpszClassName; public IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int cbSize; public Rect rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct InitCommonControls { public uint Size, Classes; }
    [StructLayout(LayoutKind.Sequential)] private struct ToolInfo
    {
        public uint Size, Flags;
        public IntPtr Window;
        public UIntPtr Id;
        public Rect Area;
        public IntPtr Instance, Text, Parameter, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative(int w, int h) { public int Width = w, Height = h; }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Red, Green, Blue; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool InitCommonControlsEx(ref InitCommonControls controls);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendTooltipMessage(IntPtr hwnd, uint message, IntPtr wParam, ref ToolInfo info);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendTooltipMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage message, IntPtr hwnd, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint first, uint last, uint remove);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint MsgWaitForMultipleObjectsEx(uint count, ref IntPtr handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint interval, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint desiredAccess);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref PointNative dst, ref SizeNative size, IntPtr srcDc, ref PointNative src, uint colorKey, ref BlendFunction blend, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public PointNative pt; public uint lPrivate; }
}
