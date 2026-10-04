using System.Diagnostics;
using Microsoft.Win32;

namespace Dianbo.App.Services;

public static class AutoStartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "DianboMusic";

    /// <summary>检查当前用户注册表中是否已配置自启动。</summary>
    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppValueName) != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read autostart key: {ex.Message}");
            return false;
        }
    }

    /// <summary>设置或取消开机自启动</summary>
    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppValueName, $"\"{exePath}\" --autostart");
                }
            }
            else
            {
                key.DeleteValue(AppValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write autostart key: {ex.Message}");
        }
    }
}
