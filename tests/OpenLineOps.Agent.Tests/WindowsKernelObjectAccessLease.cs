using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenLineOps.Agent.Tests;

[SupportedOSPlatform("windows")]
internal sealed class WindowsKernelObjectAccessLease : IDisposable
{
    private const uint DaclSecurityInformation = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumSecurityDescriptorBytes = 64 * 1024;
    private const int MaximumAceCount = 1_024;

    private const ControlFlags DaclControlFlags =
        ControlFlags.DiscretionaryAclPresent
        | ControlFlags.DiscretionaryAclDefaulted
        | ControlFlags.DiscretionaryAclAutoInheritRequired
        | ControlFlags.DiscretionaryAclAutoInherited
        | ControlFlags.DiscretionaryAclProtected;

    private readonly SafeHandle _handle;
    private readonly byte[] _originalDescriptor;
    private readonly SecurityIdentifier _grantee;
    private readonly int _accessMask;
    private readonly int _insertedAceIndex;
    private readonly string _objectDescription;
    private byte[]? _installedDescriptor;
    private bool _accessApplied;
    private bool _disposed;

    private WindowsKernelObjectAccessLease(
        SafeHandle handle,
        byte[] originalDescriptor,
        SecurityIdentifier grantee,
        int accessMask,
        int insertedAceIndex,
        string objectDescription)
    {
        _handle = handle;
        _originalDescriptor = originalDescriptor;
        _grantee = grantee;
        _accessMask = accessMask;
        _insertedAceIndex = insertedAceIndex;
        _objectDescription = objectDescription;
    }

    public static WindowsKernelObjectAccessLease AcquireOwnedHandle(
        SafeHandle ownedHandle,
        SecurityIdentifier grantee,
        int accessMask,
        string objectDescription)
    {
        ArgumentNullException.ThrowIfNull(ownedHandle);
        ArgumentNullException.ThrowIfNull(grantee);
        if (ownedHandle.IsInvalid || ownedHandle.IsClosed)
        {
            ownedHandle.Dispose();
            throw new ArgumentException(
                "A valid owned kernel-object handle is required.",
                nameof(ownedHandle));
        }
        if (accessMask == 0)
        {
            ownedHandle.Dispose();
            throw new ArgumentOutOfRangeException(
                nameof(accessMask),
                "The scoped access mask must be nonzero.");
        }
        if (string.IsNullOrWhiteSpace(objectDescription)
            || objectDescription.Any(char.IsControl))
        {
            ownedHandle.Dispose();
            throw new ArgumentException(
                "The kernel-object description must be non-empty control-free text.",
                nameof(objectDescription));
        }

        WindowsKernelObjectAccessLease? lease = null;
        try
        {
            var originalDescriptor = ReadSecurityDescriptor(
                ownedHandle,
                objectDescription);
            var original = ParseDescriptor(originalDescriptor, objectDescription);
            var originalDacl = RequireBoundedDacl(original, objectDescription);
            if (originalDacl
                .OfType<KnownAce>()
                .Any(ace => grantee.Equals(ace.SecurityIdentifier)))
            {
                throw new InvalidDataException(
                    $"The {objectDescription} DACL already mentions the one-shot bridge service SID.");
            }

            var insertedAceIndex = FirstInheritedAceIndex(originalDacl);
            lease = new WindowsKernelObjectAccessLease(
                ownedHandle,
                originalDescriptor,
                grantee,
                accessMask,
                insertedAceIndex,
                objectDescription);
            lease.Apply(original, originalDacl);
            return lease;
        }
        catch (Exception operationFailure)
        {
            if (lease is null)
            {
                ownedHandle.Dispose();
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
                lease._handle.Dispose();
                lease._disposed = true;
            }

            if (restorationFailure is not null)
            {
                throw new AggregateException(
                    $"Applying scoped access to the {objectDescription} failed, and its original DACL could not be restored.",
                    operationFailure,
                    restorationFailure);
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
            throw;
        }
    }

    public static string ReadDaclSddl(
        SafeHandle handle,
        string objectDescription)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var descriptor = ParseDescriptor(
            ReadSecurityDescriptor(handle, objectDescription),
            objectDescription);
        _ = RequireBoundedDacl(descriptor, objectDescription);
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }

    public static bool HasExactAce(
        SafeHandle handle,
        SecurityIdentifier grantee,
        int accessMask,
        string objectDescription)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(grantee);
        var dacl = RequireBoundedDacl(
            ParseDescriptor(
                ReadSecurityDescriptor(handle, objectDescription),
                objectDescription),
            objectDescription);
        var matches = dacl
            .OfType<CommonAce>()
            .Where(ace => grantee.Equals(ace.SecurityIdentifier))
            .ToArray();
        return matches.Length == 1
               && IsExactAce(matches[0], grantee, accessMask);
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
            _handle.Dispose();
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
                _accessMask,
                _grantee,
                isCallback: false,
                opaque: null));
        var updated = new RawSecurityDescriptor(
            original.ControlFlags,
            original.Owner,
            original.Group,
            original.SystemAcl,
            updatedDacl);
        var updatedBytes = ToBinary(updated, _objectDescription);
        _installedDescriptor = updatedBytes;
        if (!SetKernelObjectSecurity(
                _handle,
                DaclSecurityInformation,
                updatedBytes))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not apply scoped access to the {_objectDescription}; Win32 error {error}.");
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

        var installedDescriptor = _installedDescriptor
                                  ?? throw new InvalidOperationException(
                                      $"The {_objectDescription} lease has no installed DACL snapshot.");
        var installed = ParseDescriptor(installedDescriptor, _objectDescription);
        var current = ParseDescriptor(
            ReadSecurityDescriptor(_handle, _objectDescription),
            _objectDescription);
        if (!AreEquivalentDacls(
                RequireBoundedDacl(installed, _objectDescription),
                installed.ControlFlags,
                RequireBoundedDacl(current, _objectDescription),
                current.ControlFlags))
        {
            RemoveScopedAccessAfterDaclDrift(current);
        }

        if (!SetKernelObjectSecurity(
                _handle,
                DaclSecurityInformation,
                _originalDescriptor))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not restore the exact {_objectDescription} DACL; Win32 error {error}.");
        }

        var expected = ParseDescriptor(_originalDescriptor, _objectDescription);
        var actual = ParseDescriptor(
            ReadSecurityDescriptor(_handle, _objectDescription),
            _objectDescription);
        AssertEquivalentDacl(
            RequireBoundedDacl(expected, _objectDescription),
            expected.ControlFlags,
            RequireBoundedDacl(actual, _objectDescription),
            actual.ControlFlags,
            $"The {_objectDescription} DACL did not return to its exact pre-bridge state.");
        _accessApplied = false;
    }

    private void RemoveScopedAccessAfterDaclDrift(
        RawSecurityDescriptor current)
    {
        var currentDacl = RequireBoundedDacl(current, _objectDescription);
        var retainedAces = new List<GenericAce>(currentDacl.Count);
        var removedScopedAce = false;
        foreach (GenericAce ace in currentDacl)
        {
            if (!removedScopedAce
                && ace is CommonAce common
                && IsExactAce(common, _grantee, _accessMask))
            {
                removedScopedAce = true;
                continue;
            }

            retainedAces.Add(CloneAce(ace));
        }

        if (!removedScopedAce)
        {
            _accessApplied = false;
            throw new InvalidDataException(
                $"The {_objectDescription} DACL changed while scoped access was active; the exact scoped ACE was already absent, so the original snapshot was not written over the external change.");
        }

        var cleanedDacl = new RawAcl(currentDacl.Revision, retainedAces.Count);
        for (var index = 0; index < retainedAces.Count; index++)
        {
            cleanedDacl.InsertAce(index, retainedAces[index]);
        }

        var cleaned = new RawSecurityDescriptor(
            current.ControlFlags,
            current.Owner,
            current.Group,
            current.SystemAcl,
            cleanedDacl);
        if (!SetKernelObjectSecurity(
                _handle,
                DaclSecurityInformation,
                ToBinary(cleaned, _objectDescription)))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"The {_objectDescription} DACL drifted, and the exact scoped ACE could not be removed; Win32 error {error}.");
        }

        var verified = ParseDescriptor(
            ReadSecurityDescriptor(_handle, _objectDescription),
            _objectDescription);
        var verifiedDacl = RequireBoundedDacl(verified, _objectDescription);
        if (!AreEquivalentDacls(
                cleanedDacl,
                cleaned.ControlFlags,
                verifiedDacl,
                verified.ControlFlags))
        {
            throw new InvalidDataException(
                $"The {_objectDescription} DACL drifted, and removal of the exact scoped ACE could not be verified.");
        }

        _accessApplied = false;
        throw new InvalidDataException(
            $"The {_objectDescription} DACL changed while scoped access was active; the exact scoped ACE was removed without overwriting the external change, and the gate must fail.");
    }

    private void VerifyTemporaryDacl(
        RawSecurityDescriptor original,
        RawAcl originalDacl)
    {
        var actual = ParseDescriptor(
            ReadSecurityDescriptor(_handle, _objectDescription),
            _objectDescription);
        var actualDacl = RequireBoundedDacl(actual, _objectDescription);
        if ((actual.ControlFlags & DaclControlFlags)
            != (original.ControlFlags & DaclControlFlags))
        {
            throw new InvalidDataException(
                $"Scoped access changed {_objectDescription} DACL control flags.");
        }
        if (actualDacl.Revision != originalDacl.Revision
            || actualDacl.Count != originalDacl.Count + 1)
        {
            throw new InvalidDataException(
                $"Scoped access did not add exactly one {_objectDescription} ACE.");
        }

        for (var actualIndex = 0; actualIndex < actualDacl.Count; actualIndex++)
        {
            if (actualIndex == _insertedAceIndex)
            {
                if (actualDacl[actualIndex] is not CommonAce added
                    || !IsExactAce(added, _grantee, _accessMask))
                {
                    throw new InvalidDataException(
                        $"Scoped access added an unexpected {_objectDescription} ACE.");
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
                    $"Scoped access changed an existing {_objectDescription} ACE.");
            }
        }
    }

    private static bool IsExactAce(
        CommonAce ace,
        SecurityIdentifier grantee,
        int accessMask) =>
        ace.AceFlags == AceFlags.None
        && ace.AceQualifier == AceQualifier.AccessAllowed
        && ace.AccessMask == accessMask
        && grantee.Equals(ace.SecurityIdentifier)
        && !ace.IsCallback
        && ace.GetOpaque() is not { Length: > 0 };

    private static void AssertEquivalentDacl(
        RawAcl expected,
        ControlFlags expectedControlFlags,
        RawAcl actual,
        ControlFlags actualControlFlags,
        string message)
    {
        if (!AreEquivalentDacls(
                expected,
                expectedControlFlags,
                actual,
                actualControlFlags))
        {
            throw new InvalidDataException(message);
        }
    }

    private static bool AreEquivalentDacls(
        RawAcl expected,
        ControlFlags expectedControlFlags,
        RawAcl actual,
        ControlFlags actualControlFlags) =>
        (actualControlFlags & DaclControlFlags)
        == (expectedControlFlags & DaclControlFlags)
        && actual.Revision == expected.Revision
        && AclBytes(actual).AsSpan().SequenceEqual(AclBytes(expected));

    private static byte[] ReadSecurityDescriptor(
        SafeHandle handle,
        string objectDescription)
    {
        _ = GetKernelObjectSecurity(
            handle,
            DaclSecurityInformation,
            null,
            length: 0,
            out var requiredLength);
        var error = Marshal.GetLastPInvokeError();
        if (requiredLength == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(
                error,
                $"Could not determine the {objectDescription} security descriptor size; Win32 error {error}.");
        }
        if (requiredLength > MaximumSecurityDescriptorBytes)
        {
            throw new InvalidDataException(
                $"The {objectDescription} security descriptor is unexpectedly large.");
        }

        var descriptor = new byte[checked((int)requiredLength)];
        if (!GetKernelObjectSecurity(
                handle,
                DaclSecurityInformation,
                descriptor,
                requiredLength,
                out _))
        {
            error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not read the {objectDescription} security descriptor; Win32 error {error}.");
        }

        return descriptor;
    }

    private static RawSecurityDescriptor ParseDescriptor(
        byte[] descriptor,
        string objectDescription)
    {
        try
        {
            return new RawSecurityDescriptor(descriptor, offset: 0);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"The {objectDescription} security descriptor is malformed.",
                exception);
        }
    }

    private static RawAcl RequireBoundedDacl(
        RawSecurityDescriptor descriptor,
        string objectDescription)
    {
        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0
            || descriptor.DiscretionaryAcl is not { } dacl)
        {
            throw new InvalidDataException(
                $"The {objectDescription} must have an explicit bounded DACL.");
        }
        if (dacl.Count > MaximumAceCount)
        {
            throw new InvalidDataException(
                $"The {objectDescription} DACL contains unexpectedly many ACEs.");
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

    private static byte[] ToBinary(
        RawSecurityDescriptor descriptor,
        string objectDescription)
    {
        if (descriptor.BinaryLength > MaximumSecurityDescriptorBytes)
        {
            throw new InvalidDataException(
                $"The scoped {objectDescription} security descriptor is unexpectedly large.");
        }

        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, offset: 0);
        return bytes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeHandle handle,
        uint requestedInformation,
        [Out] byte[]? securityDescriptor,
        uint length,
        out uint lengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        SafeHandle handle,
        uint securityInformation,
        byte[] securityDescriptor);
}
