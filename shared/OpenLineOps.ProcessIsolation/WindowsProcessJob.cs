using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.ProcessIsolation;

public sealed record WindowsProcessLimits(
    int ActiveProcessLimit,
    long ProcessMemoryLimitBytes,
    long JobMemoryLimitBytes,
    TimeSpan CpuTimeLimit)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ActiveProcessLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ProcessMemoryLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(JobMemoryLimitBytes);
        if (CpuTimeLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CpuTimeLimit),
                "External program CPU time limit must be positive.");
        }

        if (Environment.Is64BitProcess)
        {
            return;
        }

        if (ProcessMemoryLimitBytes > uint.MaxValue || JobMemoryLimitBytes > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProcessMemoryLimitBytes),
                "External program memory limits must fit the native process address size.");
        }
    }
}

internal sealed class WindowsProcessJob : IDisposable
{
    private const uint JobObjectLimitJobTime = 0x00000004;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    private readonly SafeJobHandle _handle;

    private WindowsProcessJob(SafeJobHandle handle)
    {
        _handle = handle;
    }

    public static WindowsProcessJob Create(WindowsProcessLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows process jobs require Windows.");
        }

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not create the external program Job Object.");
        }

        var job = new WindowsProcessJob(handle);
        try
        {
            job.Configure(limits);
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Assign(SafeProcessHandle processHandle)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        if (processHandle.IsInvalid || processHandle.IsClosed)
        {
            throw new ArgumentException(
                "External program process handle must be open.",
                nameof(processHandle));
        }

        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not assign the suspended external program to its Job Object. "
                + "The host may already belong to a Job Object that prohibits nested jobs.");
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private void Configure(WindowsProcessLimits limits)
    {
        var information = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                PerJobUserTimeLimit = limits.CpuTimeLimit.Ticks,
                LimitFlags = JobObjectLimitKillOnJobClose
                             | JobObjectLimitActiveProcess
                             | JobObjectLimitProcessMemory
                             | JobObjectLimitJobMemory
                             | JobObjectLimitJobTime,
                ActiveProcessLimit = checked((uint)limits.ActiveProcessLimit)
            },
            ProcessMemoryLimit = checked((nuint)limits.ProcessMemoryLimitBytes),
            JobMemoryLimit = checked((nuint)limits.JobMemoryLimitBytes)
        };

        var length = Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(
                    _handle,
                    JobObjectExtendedLimitInformation,
                    buffer,
                    checked((uint)length)))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not configure the external program Job Object.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeJobHandle job,
        SafeProcessHandle process);

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return CloseHandle(handle);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
