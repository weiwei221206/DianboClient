using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dianbo.App.Services;

/// <summary>单实例保护</summary>
public static class SingleInstance
{
    private static string SafeUserName
    {
        get
        {
            var user = Environment.UserName;
            if (string.IsNullOrWhiteSpace(user)) return "DefaultUser";
            var chars = user.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars);
        }
    }

    internal static string MutexName =>
        $"Local\\DianboClient.SingleInstance.{SafeUserName}";

    private static Mutex? _mutex;

    public static bool TryBootstrap(Func<Microsoft.UI.Xaml.Window> createWindow, out Microsoft.UI.Xaml.Window? window)
    {
        window = null;
        _mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        window = createWindow();
        return true;
    }

    private static readonly uint WmDianboActivate = RegisterWindowMessage("DianboMusic.Activate");
    private const IntPtr HWND_BROADCAST = (IntPtr)0xffff;

    private static void ActivateExistingWindow()
    {
        try
        {
            PostMessage(HWND_BROADCAST, WmDianboActivate, IntPtr.Zero, IntPtr.Zero);

            var currentId = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                if (process.Id == currentId) continue;
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
                return;
            }
        }
        catch (Exception)
        {
            // 前台激活失败不影响"只保留一个实例"这一结果。
        }
    }

    private static string ProcessName
    {
        get
        {
            try
            {
                return Process.GetCurrentProcess().ProcessName;
            }
            catch (Exception)
            {
                return "Dianbo";
            }
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
