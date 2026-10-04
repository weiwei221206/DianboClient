using Dianbo.App.Services;
using Microsoft.UI.Xaml;

namespace Dianbo.App;

public partial class App : Application
{
    private Window? _window;

    /// <summary>窗口与页面共用同一实例</summary>
    public static AppServices? Services { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ShellIdentity.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupLog.Write($"unhandled domain exception: {e.ExceptionObject}");
        UnhandledException += (_, e) =>
        {
            StartupLog.Write($"unhandled xaml exception: {e.Message} | {e.Exception}");
            e.Handled = true;
        };

        StartupLog.Write("OnLaunched begin");

        if (!SingleInstance.TryBootstrap(CreateMainWindow, out var window))
        {
            StartupLog.Write("another instance is already running; exiting");
            Exit();
            return;
        }

        _window = window;
        if (_window is null)
        {
            StartupLog.Write("window creation failed");
            Exit();
            return;
        }

        var cmdArgs = Environment.GetCommandLineArgs();
        var isAutoStart = cmdArgs.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));

        if (isAutoStart && Services?.Settings.SilentAutoStart == true)
        {
            _window.Activate();
            _window.AppWindow.Hide();
            StartupLog.Write("window activated silently in tray");
        }
        else
        {
            _window.Activate();
            StartupLog.Write("window activated");
        }

        var pageArg = cmdArgs.SkipWhile(a => !string.Equals(a, "--page", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        if (!string.IsNullOrEmpty(pageArg) && _window is MainWindow mainWin)
        {
            mainWin.Navigate(pageArg);
        }
    }

    private static Window CreateMainWindow()
    {
        Services = AppServices.Create();
        StartupLog.Write("services created");
        var window = new MainWindow(Services);
        StartupLog.Write("window constructed");
        return window;
    }
}

/// <summary>启动期诊断日志</summary>
internal static class StartupLog
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BodianClient",
        "startup.log");

    public static void Write(string message)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
            System.IO.File.AppendAllText(Path, $"{DateTimeOffset.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
