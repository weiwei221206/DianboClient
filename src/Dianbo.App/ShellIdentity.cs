using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Dianbo.App;

internal static class ShellIdentity
{
    internal const string AppId = "Dianbo.App";
    private static readonly Guid AppModelPropertySet = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    internal static void Initialize()
    {
        try
        {
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
            EnsureStartMenuShortcut();
        }
        catch (Exception error)
        {
            StartupLog.Write($"app identity unavailable: {error.GetType().Name}");
        }
    }

    private static void EnsureStartMenuShortcut()
    {
        var exePath = Environment.ProcessPath;
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(startMenu)) return;

        var shortcutPath = Path.Combine(startMenu, "点波音乐.lnk");
        if (File.Exists(shortcutPath)) return;

        object? shellLink = null;
        try
        {
            Directory.CreateDirectory(startMenu);
            var shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))
                ?? throw new InvalidOperationException("Shell link is unavailable.");
            shellLink = Activator.CreateInstance(shellLinkType)
                ?? throw new InvalidOperationException("Shell link could not be created.");

            var link = (IShellLinkW)shellLink;
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? string.Empty);
            link.SetDescription("点波音乐");
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath)) link.SetIconLocation(iconPath, 0);

            var propertyStore = (IPropertyStore)shellLink;
            var key = new PropertyKey(AppModelPropertySet, 5);
            var value = new PropVariant(AppId);
            try
            {
                propertyStore.SetValue(ref key, ref value);
                propertyStore.Commit();
            }
            finally
            {
                value.Dispose();
            }

            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        finally
        {
            if (shellLink is not null) Marshal.ReleaseComObject(shellLink);
        }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath();
        void GetIDList();
        void SetIDList();
        void GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory();
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments();
        void SetArguments();
        void GetHotkey();
        void SetHotkey();
        void GetShowCmd();
        void SetShowCmd();
        void GetIconLocation();
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath();
        void Resolve();
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount();
        void GetAt();
        void GetValue();
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant(string value)
    {
        [FieldOffset(0)] public ushort VariantType = 31;
        [FieldOffset(8)] public IntPtr StringPointer = Marshal.StringToCoTaskMemUni(value);

        public void Dispose()
        {
            Marshal.FreeCoTaskMem(StringPointer);
            StringPointer = IntPtr.Zero;
        }
    }
}
