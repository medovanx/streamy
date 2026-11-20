using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Streamy.Services;

/// <summary>
/// Puts the adb processes Streamy starts in a Windows job object that ends them when Streamy ends, even if it
/// crashes or is killed. Without it a long-running one (the notification watch, a session's server shell) would keep
/// running, and so would its loop on the phone. Processes those start (the adb server, which other tools share)
/// are left out of the job, so closing Streamy never stops the adb server.
/// </summary>
public static class ChildProcesses
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    // Never closed: the handle closes when Streamy's process ends, and that is what ends the children.
    private static readonly Lazy<IntPtr> Job = new(Create);

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimits>()))
        {
            Log.Info("job object: " + new Win32Exception().Message);
            return IntPtr.Zero;
        }
        return job;
    }

    /// <summary>Ties the process to Streamy's lifetime. Harmless if it fails (older Windows, nested jobs).</summary>
    public static void Track(Process p)
    {
        try
        {
            if (Job.Value != IntPtr.Zero) AssignProcessToJobObject(Job.Value, p.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }
}
