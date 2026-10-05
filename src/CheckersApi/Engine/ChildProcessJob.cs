using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CheckersApi.Engine;

/// <summary>
/// On Windows, puts worker processes into a Job Object with "kill on job close", so they end together with
/// the IIS worker process (w3wp.exe) even if it is terminated abruptly. Workers also exit by themselves
/// when their stdin closes; this is the second line of defence.
/// </summary>
static class ChildProcessJob
{
    static readonly object Lock = new();
    static IntPtr _job;
    static bool _failed;

    public static void Add(Process process, ILogger log)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Lock)
        {
            if (_failed) return;
            try
            {
                if (_job == IntPtr.Zero) _job = CreateKillOnCloseJob();
                if (!AssignProcessToJobObject(_job, process.Handle))
                    log.LogWarning("Could not add worker pid {Pid} to the job object (error {Error}); it will still exit when the app closes its stdin.",
                        process.Id, Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                _failed = true;
                log.LogWarning(ex, "Job object not available; workers rely on stdin closing to exit.");
            }
        }
    }

    static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastWin32Error()}");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                throw new InvalidOperationException($"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
