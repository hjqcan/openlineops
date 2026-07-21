using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.Agent.Tests;

[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessAccessLease : IDisposable
{
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint Synchronize = 0x00100000;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumSecurityDescriptorBytes = 64 * 1024;
    private const int MaximumAceCount = 1_024;

    private const ControlFlags DaclControlFlags =
        ControlFlags.DiscretionaryAclPresent
        | ControlFlags.DiscretionaryAclDefaulted
        | ControlFlags.DiscretionaryAclAutoInheritRequired
        | ControlFlags.DiscretionaryAclAutoInherited
        | ControlFlags.DiscretionaryAclProtected;

    private readonly SafeProcessHandle _processHandle;
    private readonly byte[] _originalDescriptor;
    private readonly SecurityIdentifier _bridgeServiceSid;
    private readonly int _insertedAceIndex;
    private bool _accessApplied;
    private bool _disposed;

    private WindowsProcessAccessLease(
        SafeProcessHandle processHandle,
        byte[] originalDescriptor,
        SecurityIdentifier bridgeServiceSid,
        int insertedAceIndex)
    {
        _processHandle = processHandle;
        _originalDescriptor = originalDescriptor;
        _bridgeServiceSid = bridgeServiceSid;
        _insertedAceIndex = insertedAceIndex;
    }

    public static WindowsProcessAccessLease Acquire(
        SafeProcessHandle retainedProcessHandle,
        uint processId,
        long expectedCreatedAtUtcTicks,
        SecurityIdentifier bridgeServiceSid)
    {
        ArgumentNullException.ThrowIfNull(retainedProcessHandle);
        ArgumentNullException.ThrowIfNull(bridgeServiceSid);
        if (processId == 0
            || retainedProcessHandle.IsInvalid
            || retainedProcessHandle.IsClosed)
        {
            throw new ArgumentException(
                "A live retained source process handle is required.",
                nameof(retainedProcessHandle));
        }

        ValidateExactProcess(
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

        WindowsProcessAccessLease? lease = null;
        try
        {
            ValidateExactProcess(
                securityHandle,
                processId,
                expectedCreatedAtUtcTicks,
                "process-DACL source Station");
            var originalDescriptor = ReadSecurityDescriptor(securityHandle);
            var original = ParseDescriptor(originalDescriptor);
            var originalDacl = RequireBoundedDacl(original);
            if (originalDacl
                .OfType<QualifiedAce>()
                .Any(ace => bridgeServiceSid.Equals(ace.SecurityIdentifier)))
            {
                throw new InvalidDataException(
                    "The source Station process DACL already mentions the one-shot bridge service SID.");
            }

            var insertedAceIndex = FirstInheritedAceIndex(originalDacl);
            lease = new WindowsProcessAccessLease(
                securityHandle,
                originalDescriptor,
                bridgeServiceSid,
                insertedAceIndex);
            securityHandle = null!;
            lease.Apply(original, originalDacl);
            return lease;
        }
        catch (Exception operationFailure)
        {
            if (lease is null)
            {
                securityHandle.Dispose();
                throw;
            }

            Exception? restorationFailure = null;
            try
            {
                lease.RestoreRequired();
            }
            catch (Exception exception)
            {
                restorationFailure = exception;
            }
            finally
            {
                lease._processHandle.Dispose();
                lease._disposed = true;
            }

            if (restorationFailure is not null)
            {
                throw new AggregateException(
                    "Applying the scoped source-process access lease failed, and its original DACL could not be restored.",
                    operationFailure,
                    restorationFailure);
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
            throw;
        }
    }

    internal static string ReadDaclSddl(SafeProcessHandle processHandle)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        var descriptor = ParseDescriptor(ReadSecurityDescriptor(processHandle));
        _ = RequireBoundedDacl(descriptor);
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }

    internal static bool HasExactQueryAndWaitAce(
        SafeProcessHandle processHandle,
        SecurityIdentifier bridgeServiceSid)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        ArgumentNullException.ThrowIfNull(bridgeServiceSid);
        var dacl = RequireBoundedDacl(
            ParseDescriptor(ReadSecurityDescriptor(processHandle)));
        var matches = dacl
            .OfType<CommonAce>()
            .Where(ace => bridgeServiceSid.Equals(ace.SecurityIdentifier))
            .ToArray();
        return matches.Length == 1 && IsExactQueryAndWaitAce(matches[0], bridgeServiceSid);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Exception? restorationFailure = null;
        try
        {
            RestoreRequired();
        }
        catch (Exception exception)
        {
            restorationFailure = exception;
        }
        finally
        {
            _processHandle.Dispose();
            _disposed = true;
        }

        if (restorationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(restorationFailure).Throw();
        }
    }

    private void Apply(
        RawSecurityDescriptor original,
        RawAcl originalDacl)
    {
        var updatedDacl = new RawAcl(
            originalDacl.Revision,
            checked(originalDacl.Count + 1));
        for (var index = 0; index < originalDacl.Count; index++)
        {
            updatedDacl.InsertAce(index, CloneAce(originalDacl[index]));
        }

        updatedDacl.InsertAce(
            _insertedAceIndex,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                unchecked((int)(ProcessQueryLimitedInformation | Synchronize)),
                _bridgeServiceSid,
                isCallback: false,
                opaque: null));
        var updated = new RawSecurityDescriptor(
            original.ControlFlags,
            original.Owner,
            original.Group,
            original.SystemAcl,
            updatedDacl);
        var updatedBytes = ToBinary(updated);
        if (!SetKernelObjectSecurity(
                _processHandle,
                DaclSecurityInformation,
                updatedBytes))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not apply the scoped source Station process-DACL lease; Win32 error {error}.");
        }

        _accessApplied = true;
        VerifyTemporaryDacl(original, originalDacl);
    }

    private void RestoreRequired()
    {
        if (!_accessApplied)
        {
            return;
        }

        if (!SetKernelObjectSecurity(
                _processHandle,
                DaclSecurityInformation,
                _originalDescriptor))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not restore the exact source Station process DACL; Win32 error {error}.");
        }

        var expected = ParseDescriptor(_originalDescriptor);
        var actual = ParseDescriptor(ReadSecurityDescriptor(_processHandle));
        AssertEquivalentDacl(
            RequireBoundedDacl(expected),
            expected.ControlFlags,
            RequireBoundedDacl(actual),
            actual.ControlFlags,
            "The source Station process DACL did not return to its exact pre-bridge state.");
        _accessApplied = false;
    }

    private void VerifyTemporaryDacl(
        RawSecurityDescriptor original,
        RawAcl originalDacl)
    {
        var actual = ParseDescriptor(ReadSecurityDescriptor(_processHandle));
        var actualDacl = RequireBoundedDacl(actual);
        if ((actual.ControlFlags & DaclControlFlags)
            != (original.ControlFlags & DaclControlFlags))
        {
            throw new InvalidDataException(
                "The scoped source Station process-DACL lease changed DACL control flags.");
        }
        if (actualDacl.Revision != originalDacl.Revision
            || actualDacl.Count != originalDacl.Count + 1)
        {
            throw new InvalidDataException(
                "The scoped source Station process-DACL lease did not add exactly one ACE.");
        }

        for (var actualIndex = 0; actualIndex < actualDacl.Count; actualIndex++)
        {
            if (actualIndex == _insertedAceIndex)
            {
                if (actualDacl[actualIndex] is not CommonAce added
                    || !IsExactQueryAndWaitAce(added, _bridgeServiceSid))
                {
                    throw new InvalidDataException(
                        "The scoped source Station process-DACL lease added an unexpected ACE.");
                }

                continue;
            }

            var originalIndex = actualIndex < _insertedAceIndex
                ? actualIndex
                : actualIndex - 1;
            if (!AceBytes(actualDacl[actualIndex])
                .AsSpan()
                .SequenceEqual(AceBytes(originalDacl[originalIndex])))
            {
                throw new InvalidDataException(
                    "The scoped source Station process-DACL lease changed an existing ACE.");
            }
        }
    }

    private static bool IsExactQueryAndWaitAce(
        CommonAce ace,
        SecurityIdentifier bridgeServiceSid) =>
        ace.AceFlags == AceFlags.None
        && ace.AceQualifier == AceQualifier.AccessAllowed
        && ace.AccessMask
        == unchecked((int)(ProcessQueryLimitedInformation | Synchronize))
        && bridgeServiceSid.Equals(ace.SecurityIdentifier)
        && !ace.IsCallback
        && ace.GetOpaque() is not { Length: > 0 };

    private static void AssertEquivalentDacl(
        RawAcl expected,
        ControlFlags expectedControlFlags,
        RawAcl actual,
        ControlFlags actualControlFlags,
        string message)
    {
        if ((actualControlFlags & DaclControlFlags)
                != (expectedControlFlags & DaclControlFlags)
            || actual.Revision != expected.Revision
            || !AclBytes(actual).AsSpan().SequenceEqual(AclBytes(expected)))
        {
            throw new InvalidDataException(message);
        }
    }

    private static void ValidateExactProcess(
        SafeProcessHandle processHandle,
        uint expectedProcessId,
        long expectedCreatedAtUtcTicks,
        string role)
    {
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
                $"The {role} PID {expectedProcessId} creation time changed before its process-DACL lease.");
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

    private static byte[] ReadSecurityDescriptor(SafeProcessHandle processHandle)
    {
        _ = GetKernelObjectSecurity(
            processHandle,
            DaclSecurityInformation,
            null,
            length: 0,
            out var requiredLength);
        var error = Marshal.GetLastPInvokeError();
        if (requiredLength == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(
                error,
                $"Could not determine the source Station process security descriptor size; Win32 error {error}.");
        }
        if (requiredLength > MaximumSecurityDescriptorBytes)
        {
            throw new InvalidDataException(
                "The source Station process security descriptor is unexpectedly large.");
        }

        var descriptor = new byte[checked((int)requiredLength)];
        if (!GetKernelObjectSecurity(
                processHandle,
                DaclSecurityInformation,
                descriptor,
                requiredLength,
                out _))
        {
            error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not read the source Station process security descriptor; Win32 error {error}.");
        }

        return descriptor;
    }

    private static RawSecurityDescriptor ParseDescriptor(byte[] descriptor)
    {
        try
        {
            return new RawSecurityDescriptor(descriptor, offset: 0);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The source Station process security descriptor is malformed.",
                exception);
        }
    }

    private static RawAcl RequireBoundedDacl(RawSecurityDescriptor descriptor)
    {
        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0
            || descriptor.DiscretionaryAcl is not { } dacl)
        {
            throw new InvalidDataException(
                "The source Station process must have an explicit bounded DACL.");
        }
        if (dacl.Count > MaximumAceCount)
        {
            throw new InvalidDataException(
                "The source Station process DACL contains unexpectedly many ACEs.");
        }

        return dacl;
    }

    private static int FirstInheritedAceIndex(RawAcl dacl)
    {
        for (var index = 0; index < dacl.Count; index++)
        {
            if ((dacl[index].AceFlags & AceFlags.Inherited) != 0)
            {
                return index;
            }
        }

        return dacl.Count;
    }

    private static GenericAce CloneAce(GenericAce ace) =>
        GenericAce.CreateFromBinaryForm(AceBytes(ace), offset: 0);

    private static byte[] AceBytes(GenericAce ace)
    {
        var bytes = new byte[ace.BinaryLength];
        ace.GetBinaryForm(bytes, offset: 0);
        return bytes;
    }

    private static byte[] AclBytes(RawAcl acl)
    {
        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, offset: 0);
        return bytes;
    }

    private static byte[] ToBinary(RawSecurityDescriptor descriptor)
    {
        if (descriptor.BinaryLength > MaximumSecurityDescriptorBytes)
        {
            throw new InvalidDataException(
                "The scoped source Station process security descriptor is unexpectedly large.");
        }

        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, offset: 0);
        return bytes;
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

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeProcessHandle handle,
        uint requestedInformation,
        [Out] byte[]? securityDescriptor,
        uint length,
        out uint lengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        SafeProcessHandle handle,
        uint securityInformation,
        byte[] securityDescriptor);
}
