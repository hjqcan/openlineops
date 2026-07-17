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
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint TerminationExitCode = 0xC000013A;

    private readonly SafeJobHandle _handle;

    private WindowsProcessJob(SafeJobHandle handle)
    {
        _handle = handle;
    }

    public static WindowsProcessJob Create(WindowsProcessLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        var job = CreateUnconfigured();
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

    public static WindowsProcessJob CreateKillOnClose()
    {
        var job = CreateUnconfigured();
        try
        {
            job.ConfigureKillOnClose();
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private static WindowsProcessJob CreateUnconfigured()
    {
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

        return new WindowsProcessJob(handle);
    }

    public uint ActiveProcessCount
    {
        get
        {
            var length = Marshal.SizeOf<JobObjectBasicAccountingInformationData>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!QueryInformationJobObject(
                        _handle,
                        JobObjectBasicAccountingInformation,
                        buffer,
                        checked((uint)length),
                        out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not query the external-program Job Object.");
                }

                return Marshal.PtrToStructure<JobObjectBasicAccountingInformationData>(buffer)
                    .ActiveProcesses;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public TResult UseHandle<TResult>(Func<IntPtr, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_handle.IsInvalid || _handle.IsClosed)
        {
            throw new ObjectDisposedException(
                nameof(WindowsProcessJob),
                "The Windows process Job Object handle is unavailable.");
        }

        var addedReference = false;
        try
        {
            _handle.DangerousAddRef(ref addedReference);
            return action(_handle.DangerousGetHandle());
        }
        finally
        {
            if (addedReference)
            {
                _handle.DangerousRelease();
            }
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

    public void Terminate()
    {
        if (!_handle.IsClosed && !TerminateJobObject(_handle, TerminationExitCode))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not terminate the external-program Job Object.");
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

        SetExtendedLimits(information);
    }

    private void ConfigureKillOnClose()
    {
        var information = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        SetExtendedLimits(information);
    }

    private void SetExtendedLimits(JobObjectExtendedLimitInformationData information)
    {
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformationData
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }
}
