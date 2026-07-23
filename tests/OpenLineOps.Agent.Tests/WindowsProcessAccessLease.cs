using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.Agent.Tests;

[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessAccessLease : IDisposable
{
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint ProcessCreateProcess = 0x00000080;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private const string ObjectDescription = "source Station process";

    private readonly WindowsKernelObjectAccessLease _lease;

    private WindowsProcessAccessLease(WindowsKernelObjectAccessLease lease)
    {
        _lease = lease;
    }

    public static WindowsProcessAccessLease Prepare(
        SafeProcessHandle retainedProcessHandle,
        uint processId,
        long expectedCreatedAtUtcTicks,
        SecurityIdentifier bridgeServiceSid)
    {
        ArgumentNullException.ThrowIfNull(bridgeServiceSid);
        ValidateExactProcessHandle(
            retainedProcessHandle,
            processId,
            expectedCreatedAtUtcTicks,
            "retained source Station");

        var securityHandle = OpenProcess(
            ReadControl | WriteDac | ProcessQueryLimitedInformation | Synchronize,
            inheritHandle: false,
            processId);
        if (securityHandle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            securityHandle.Dispose();
            throw new Win32Exception(
                error,
                $"Could not open exact source Station PID {processId} for its scoped process-DACL lease; Win32 error {error}.");
        }

        try
        {
            ValidateExactProcessHandle(
                securityHandle,
                processId,
                expectedCreatedAtUtcTicks,
                "process-DACL source Station");
        }
        catch
        {
            securityHandle.Dispose();
            throw;
        }

        return new WindowsProcessAccessLease(
            WindowsKernelObjectAccessLease.PrepareOwnedHandle(
                securityHandle,
                bridgeServiceSid,
                unchecked((int)ProcessCreateProcess),
                ObjectDescription));
    }

    public void ApplyRequired() => _lease.ApplyRequired();

    internal static string ReadDaclSddl(SafeProcessHandle processHandle) =>
        WindowsKernelObjectAccessLease.ReadDaclSddl(
            processHandle,
            ObjectDescription);

    internal static bool HasExactRelayCreationAce(
        SafeProcessHandle processHandle,
        SecurityIdentifier bridgeServiceSid) =>
        WindowsKernelObjectAccessLease.HasExactAce(
            processHandle,
            bridgeServiceSid,
            unchecked((int)ProcessCreateProcess),
            ObjectDescription);

    public void Dispose() => _lease.Dispose();

    internal static void ValidateExactProcessHandle(
        SafeProcessHandle processHandle,
        uint expectedProcessId,
        long expectedCreatedAtUtcTicks,
        string role)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        if (expectedProcessId == 0
            || processHandle.IsInvalid
            || processHandle.IsClosed)
        {
            throw new ArgumentException(
                "A live source process handle and positive process identifier are required.",
                nameof(processHandle));
        }

        var actualProcessId = GetProcessId(processHandle);
        if (actualProcessId == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not read the {role} process identifier; Win32 error {error}.");
        }
        if (actualProcessId != expectedProcessId)
        {
            throw new InvalidDataException(
                $"The {role} handle is bound to PID {actualProcessId}, not {expectedProcessId}.");
        }

        var wait = WaitForSingleObject(processHandle, milliseconds: 0);
        if (wait == WaitObject0)
        {
            throw new InvalidOperationException(
                $"The {role} PID {expectedProcessId} has exited.");
        }
        if (wait == WaitFailed)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not inspect the {role} PID {expectedProcessId} state; Win32 error {error}.");
        }
        if (wait != WaitTimeout)
        {
            throw new InvalidOperationException(
                $"The {role} PID {expectedProcessId} returned unexpected wait status 0x{wait:x8}.");
        }

        var actualCreatedAtUtcTicks = ReadProcessCreatedAtUtcTicks(processHandle, role);
        if (actualCreatedAtUtcTicks != expectedCreatedAtUtcTicks)
        {
            throw new InvalidDataException(
                $"The {role} PID {expectedProcessId} creation time changed before its scoped access lease.");
        }
    }

    private static long ReadProcessCreatedAtUtcTicks(
        SafeProcessHandle processHandle,
        string role)
    {
        if (!GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not read the {role} process creation time; Win32 error {error}.");
        }

        return DateTime.FromFileTimeUtc(creationTime.ToLong()).Ticks;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public long ToLong() => ((long)HighDateTime << 32) | LowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeProcessHandle processHandle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle processHandle,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);
}
