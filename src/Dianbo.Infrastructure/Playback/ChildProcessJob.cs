using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dianbo.Infrastructure.Playback;

public sealed class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly Action<string>? _log;
    private IntPtr _handle;

    private ChildProcessJob(IntPtr handle, Action<string>? log)
    {
        _handle = handle;
        _log = log;
    }

    public static ChildProcessJob? TryCreate(Action<string>? log = null)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            log?.Invoke($"job=create-failed win32={Marshal.GetLastWin32Error()}");
            return null;
        }

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var information = new JobObjectExtendedLimitInformation();
            information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                log?.Invoke($"job=limit-failed win32={Marshal.GetLastWin32Error()}");
                CloseHandle(handle);
                return null;
            }
        }
        catch (Exception)
        {
            CloseHandle(handle);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new ChildProcessJob(handle, log);
    }

    public bool TryAssign(Process process)
    {
        var handle = _handle;
        if (handle == IntPtr.Zero) return false;
        try
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                _log?.Invoke($"job=assign-failed pid={process.Id} win32={Marshal.GetLastWin32Error()}");
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            _log?.Invoke($"job=assign-error pid={SafeId(process)} {exception.GetType().Name}");
            return false;
        }
    }

    private static int SafeId(Process process)
    {
        try { return process.Id; }
        catch (Exception) { return 0; }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle == IntPtr.Zero) return;
        try
        {
            CloseHandle(handle);
        }
        catch (Exception)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
