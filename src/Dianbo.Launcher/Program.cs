using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dianbo.Launcher;

/// <summary>
/// 发布包根目录的启动器：拉起 app\ 子目录里的程序主体，随后立即退出。
/// 只做启动一件事，不承载业务逻辑；参数原样透传（例如 --autostart）。
/// </summary>
internal static class Program
{
    private const string PayloadRelativePath = @"app\Dianbo.exe";
    private const string AppDisplayName = "点波音乐";
    private const uint MessageBoxIconError = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint windowHandle, string text, string caption, uint type);

    private static int Main(string[] args)
    {
        var payload = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, PayloadRelativePath));
        if (!File.Exists(payload))
        {
            ShowError($"未找到程序主体：\n{payload}\n\n请保持 app 子目录与启动器位于同一个文件夹内。");
            return 2;
        }

        try
        {
            var startInfo = new ProcessStartInfo(payload)
            {
                // 工作目录固定到程序主体所在目录，避免继承调用方的当前目录。
                WorkingDirectory = Path.GetDirectoryName(payload) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            };
            foreach (var argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                ShowError("无法启动程序主体。");
                return 3;
            }
        }
        catch (Exception error)
        {
            ShowError($"启动失败：\n{error.Message}");
            return 1;
        }

        return 0;
    }

    private static void ShowError(string message) =>
        MessageBox(0, message, AppDisplayName, MessageBoxIconError);
}
