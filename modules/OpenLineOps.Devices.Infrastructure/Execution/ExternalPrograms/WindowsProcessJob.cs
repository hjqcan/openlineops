using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;

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

    public static WindowsProcessJob Create(ExternalProgramHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows process jobs require Windows.");
        }

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the external program Job Object.");
        }

        var job = new WindowsProcessJob(handle);
        try
        {
            job.Configure(options);
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not assign the external program to its Job Object.");
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private void Configure(ExternalProgramHostOptions options)
    {
        var information = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                PerJobUserTimeLimit = checked(options.CpuTimeLimitSeconds * TimeSpan.TicksPerSecond),
                LimitFlags = JobObjectLimitKillOnJobClose
                             | JobObjectLimitActiveProcess
                             | JobObjectLimitProcessMemory
                             | JobObjectLimitJobMemory
                             | JobObjectLimitJobTime,
                ActiveProcessLimit = checked((uint)options.ActiveProcessLimit)
            },
            ProcessMemoryLimit = checked((nuint)options.ProcessMemoryLimitBytes),
            JobMemoryLimit = checked((nuint)options.ProcessMemoryLimitBytes)
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
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

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
