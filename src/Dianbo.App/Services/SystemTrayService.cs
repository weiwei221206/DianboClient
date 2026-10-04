using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Dianbo.Core.Models;
using Dianbo.Core.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Dianbo.App.Services;

public sealed class SystemTrayService : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAY_CALLBACK = WM_APP + 101;
    public static readonly uint WmDianboActivate = RegisterWindowMessage("DianboMusic.Activate");

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_NULL = 0x0000;

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_POPUP = 0x0010;

    private const int CommandShow = 1;
    private const int CommandPlayPause = 2;
    private const int CommandPrevious = 3;
    private const int CommandNext = 4;
    private const int CommandExit = 5;
    private const int CommandRepeatAll = 10;
    private const int CommandRepeatOne = 11;
    private const int CommandShuffle = 12;
    private const int CommandSequential = 13;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    private readonly IntPtr _hwnd;
    private readonly Window _window;
    private readonly PlaybackCoordinator _coordinator;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _exitAction;
    private readonly Action<PlaybackMode> _setPlayMode;
    private readonly SubclassProc _subclassProc;

    private IntPtr _hIcon = IntPtr.Zero;
    private bool _iconAdded;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public SystemTrayService(
        IntPtr hwnd,
        Window window,
        PlaybackCoordinator coordinator,
        DispatcherQueue dispatcher,
        Action exitAction,
        Action<PlaybackMode> setPlayMode)
    {
        _hwnd = hwnd;
        _window = window;
        _coordinator = coordinator;
        _dispatcher = dispatcher;
        _exitAction = exitAction;
        _setPlayMode = setPlayMode;

        _subclassProc = WindowSubclassProc;
        SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)101, UIntPtr.Zero);

        InitTrayIcon();

        _coordinator.SongStarted += OnSongStarted;
        _coordinator.SnapshotChanged += OnSnapshotChanged;
    }

    private void InitTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                if (_hIcon == IntPtr.Zero)
                {
                    _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                }
            }

            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY_CALLBACK,
                hIcon = _hIcon,
                szTip = "点波音乐"
            };

            _iconAdded = Shell_NotifyIconW(NIM_ADD, ref data);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to init system tray icon: {ex.Message}");
        }
    }

    private void OnSongStarted(object? sender, Song song)
    {
        UpdateTooltip($"{song.Name} — {song.Artist}");
    }

    private void OnSnapshotChanged(object? sender, PlaybackSnapshot snapshot)
    {
        if (_coordinator.CurrentSong is { } song)
        {
            UpdateTooltip($"{song.Name} — {song.Artist}");
        }
        else
        {
            UpdateTooltip("点波音乐");
        }
    }

    public void UpdateTooltip(string text)
    {
        if (_disposed || !_iconAdded) return;
        var tip = string.IsNullOrWhiteSpace(text) ? "点波音乐" : text;
        if (tip.Length > 120) tip = tip[..117] + "...";

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_TIP,
            szTip = tip
        };
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WmDianboActivate)
        {
            _dispatcher.TryEnqueue(RestoreAndActivateWindow);
            return IntPtr.Zero;
        }

        if (uMsg == WM_TRAY_CALLBACK)
        {
            var mouseMsg = (int)lParam;
            if (mouseMsg is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                _dispatcher.TryEnqueue(RestoreAndActivateWindow);
            }
            else if (mouseMsg is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                _dispatcher.TryEnqueue(ShowContextMenu);
            }
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void RestoreAndActivateWindow()
    {
        try
        {
            _window.AppWindow.Show();
            if (IsIconic(_hwnd))
            {
                ShowWindow(_hwnd, SW_RESTORE);
            }
            else
            {
                ShowWindow(_hwnd, SW_SHOW);
            }
            SetForegroundWindow(_hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to restore window: {ex.Message}");
        }
    }

    private void ShowContextMenu()
    {
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            var song = _coordinator.CurrentSong;
            var disabled = song is null ? MF_GRAYED : MF_STRING;
            var isPlaying = _coordinator.Snapshot.State is PlaybackState.Playing or PlaybackState.Seeking;
            var status = song is null ? "暂无播放" : isPlaying ? "正在播放" : "当前歌曲";
            var title = song is null ? status : $"{status} · {song.Name} — {song.Artist}";

            AppendMenuW(hMenu, MF_GRAYED, IntPtr.Zero, ShortMenuText(title));
            AppendMenuW(hMenu, MF_SEPARATOR, IntPtr.Zero, string.Empty);
            AppendMenuW(hMenu, MF_STRING, (IntPtr)CommandShow, "显示主窗口");
            AppendMenuW(hMenu, MF_SEPARATOR, IntPtr.Zero, string.Empty);
            AppendMenuW(hMenu, disabled, (IntPtr)CommandPlayPause, isPlaying ? "暂停播放" : "继续播放");
            AppendMenuW(hMenu, disabled, (IntPtr)CommandPrevious, "上一首");
            AppendMenuW(hMenu, disabled, (IntPtr)CommandNext, "下一首");

            var hModeMenu = CreatePopupMenu();
            if (hModeMenu != IntPtr.Zero)
            {
                var mode = _coordinator.PlayMode;
                AppendMenuW(hModeMenu, mode == PlaybackMode.RepeatAll ? MF_CHECKED : MF_STRING, (IntPtr)CommandRepeatAll, "列表循环");
                AppendMenuW(hModeMenu, mode == PlaybackMode.RepeatOne ? MF_CHECKED : MF_STRING, (IntPtr)CommandRepeatOne, "单曲循环");
                AppendMenuW(hModeMenu, mode == PlaybackMode.Shuffle ? MF_CHECKED : MF_STRING, (IntPtr)CommandShuffle, "随机播放");
                AppendMenuW(hModeMenu, mode == PlaybackMode.Sequential ? MF_CHECKED : MF_STRING, (IntPtr)CommandSequential, "顺序播放");
                if (!AppendMenuW(hMenu, MF_POPUP, hModeMenu, "播放模式"))
                {
                    DestroyMenu(hModeMenu);
                }
            }

            AppendMenuW(hMenu, MF_SEPARATOR, IntPtr.Zero, string.Empty);
            AppendMenuW(hMenu, MF_STRING, (IntPtr)CommandExit, "退出点波音乐");

            SetForegroundWindow(_hwnd);
            GetCursorPos(out var pt);
            var cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case CommandShow:
                    RestoreAndActivateWindow();
                    break;
                case CommandPlayPause:
                    _ = _coordinator.TogglePauseAsync(CancellationToken.None);
                    break;
                case CommandPrevious:
                    _ = _coordinator.PreviousAsync(CancellationToken.None);
                    break;
                case CommandNext:
                    _ = _coordinator.NextAsync(CancellationToken.None);
                    break;
                case CommandRepeatAll:
                    _setPlayMode(PlaybackMode.RepeatAll);
                    break;
                case CommandRepeatOne:
                    _setPlayMode(PlaybackMode.RepeatOne);
                    break;
                case CommandShuffle:
                    _setPlayMode(PlaybackMode.Shuffle);
                    break;
                case CommandSequential:
                    _setPlayMode(PlaybackMode.Sequential);
                    break;
                case CommandExit:
                    _exitAction();
                    break;
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    private static string ShortMenuText(string text)
    {
        const int maxLength = 42;
        var shortened = text.Length > maxLength ? text[..(maxLength - 1)] + "…" : text;
        return shortened.Replace("&", "&&", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _coordinator.SongStarted -= OnSongStarted;
        _coordinator.SnapshotChanged -= OnSnapshotChanged;

        if (_iconAdded)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _iconAdded = false;
        }

        RemoveWindowSubclass(_hwnd, _subclassProc, (UIntPtr)101);

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
