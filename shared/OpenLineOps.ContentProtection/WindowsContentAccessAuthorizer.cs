using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.ContentProtection;

public static class WindowsContentAccessAuthorizer
{
    private const int MaximumFileSystemEntries = 250_000;
    private const int MaximumDirectoryDepth = 256;
    private const int DirectoryInformationBufferSize = 64 * 1024;
    private const int MaximumSecurityDescriptorSize = 1024 * 1024;

    private const uint FileListDirectory = 0x00000001;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint ObjectDontReparse = 0x00001000;
    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenForBackupIntent = 0x00004000;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInformationClass = 9;
    private const int FileFullDirectoryInformationClass = 14;
    private const int FileFullDirectoryRestartInformationClass = 15;
    private const int FileFullDirectoryNameOffset = 68;
    private const int DaclSecurityInformation = 0x00000004;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorHandleEof = 38;
    private const int ErrorInsufficientBuffer = 122;
    private const int StatusReparsePointEncountered = unchecked((int)0xc000050b);
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint GenericExecute = 0x20000000;
    private const uint GenericAll = 0x10000000;
    private const uint FileGenericRead = 0x00120089;
    private const uint FileGenericWrite = 0x00120116;
    private const uint FileGenericExecute = 0x001200a0;
    private const uint FileAllAccess = 0x001f01ff;
    private const uint Delete = 0x00010000;
    private const uint WriteDacAccess = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint FileWriteData = 0x00000002;
    private const uint FileAppendData = 0x00000004;
    private const uint FileWriteExtendedAttributes = 0x00000010;
    private const uint FileDeleteChild = 0x00000040;
    private const uint FileWriteAttributes = 0x00000100;

    public static void GrantReadExecute(string rootDirectory, string readerSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GrantWindows(rootDirectory, readerSid, FileSystemRights.ReadAndExecute);
    }

    public static void GrantWorkspaceModify(string rootDirectory, string readerSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GrantWindows(rootDirectory, readerSid, FileSystemRights.Modify);
    }

    public static void VerifyReadExecute(string rootDirectory, string readerSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VerifyWindows(rootDirectory, readerSid, FileSystemRights.ReadAndExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void GrantWindows(
        string rootDirectory,
        string readerSid,
        FileSystemRights rights)
    {
        var root = NormalizeRootDirectory(rootDirectory);
        var identity = ParseReaderSid(readerSid);
        var volumeRoot = Path.GetPathRoot(root)
                         ?? throw new InvalidDataException(
                             "Content authorization root must have a volume root.");
        if (!root.StartsWith(volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Content authorization root must remain on its canonical volume.");
        }

        var relativePath = root.Length == volumeRoot.Length
            ? string.Empty
            : root[volumeRoot.Length..];
        var components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component is "." or ".."))
        {
            throw new InvalidDataException(
                "Content authorization root must be a canonical absolute directory.");
        }

        var boundaryHandles = new List<SafeFileHandle>(components.Length + 1);
        try
        {
            var volumeIsBoundary = components.Length == 0;
            var volumeHandle = OpenVolumeRoot(volumeRoot, volumeIsBoundary);
            boundaryHandles.Add(volumeHandle);
            ValidateDirectoryHandle(volumeHandle);

            var boundaryHandle = volumeHandle;
            for (var index = 0; index < components.Length; index++)
            {
                var isBoundary = index == components.Length - 1;
                boundaryHandle = OpenRelativeEntry(
                    boundaryHandle,
                    components[index],
                    isDirectory: true,
                    forAuthorization: isBoundary);
                boundaryHandles.Add(boundaryHandle);
                ValidateDirectoryHandle(boundaryHandle);
            }

            var entryCount = 0;
            RejectInheritedBoundaryAccess(boundaryHandle, identity);
            AuthorizeDirectory(
                boundaryHandle,
                identity,
                rights,
                depth: 0,
                ref entryCount);
            VerifyHandleAccess(
                boundaryHandle,
                identity,
                rights,
                isDirectory: true,
                requireExplicitAccess: true);
        }
        finally
        {
            for (var index = boundaryHandles.Count - 1; index >= 0; index--)
            {
                boundaryHandles[index].Dispose();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindows(
        string rootDirectory,
        string readerSid,
        FileSystemRights rights)
    {
        var root = NormalizeRootDirectory(rootDirectory);
        var identity = ParseReaderSid(readerSid);
        var volumeRoot = Path.GetPathRoot(root)
                         ?? throw new InvalidDataException(
                             "Content authorization root must have a volume root.");
        var relativePath = root.Length == volumeRoot.Length
            ? string.Empty
            : root[volumeRoot.Length..];
        var components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var boundaryHandles = new List<SafeFileHandle>(components.Length + 1);
        try
        {
            var volumeHandle = OpenVolumeRoot(
                volumeRoot,
                forAuthorization: components.Length == 0,
                requireWriteDac: false);
            boundaryHandles.Add(volumeHandle);
            ValidateDirectoryHandle(volumeHandle);

            var boundaryHandle = volumeHandle;
            for (var index = 0; index < components.Length; index++)
            {
                var isBoundary = index == components.Length - 1;
                boundaryHandle = OpenRelativeEntry(
                    boundaryHandle,
                    components[index],
                    isDirectory: true,
                    forAuthorization: isBoundary,
                    requireWriteDac: false);
                boundaryHandles.Add(boundaryHandle);
                ValidateDirectoryHandle(boundaryHandle);
            }

            var entryCount = 0;
            RejectInheritedBoundaryAccess(boundaryHandle, identity);
            VerifyDirectory(
                boundaryHandle,
                identity,
                rights,
                depth: 0,
                ref entryCount);
            VerifyHandleAccess(
                boundaryHandle,
                identity,
                rights,
                isDirectory: true,
                requireExplicitAccess: true);
        }
        finally
        {
            for (var index = boundaryHandles.Count - 1; index >= 0; index--)
            {
                boundaryHandles[index].Dispose();
            }
        }
    }

    private static string NormalizeRootDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new InvalidDataException(
                "Content authorization root must be a canonical absolute directory.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        if (!Path.IsPathFullyQualified(root))
        {
            throw new InvalidDataException(
                "Content authorization root must be a canonical absolute directory.");
        }

        return root;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier ParseReaderSid(string readerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readerSid);
        try
        {
            return new SecurityIdentifier(readerSid);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Content authorization reader SID is invalid.", exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenVolumeRoot(
        string volumeRoot,
        bool forAuthorization,
        bool requireWriteDac = true)
    {
        var desiredAccess = FileTraverse | FileReadAttributes;
        if (forAuthorization)
        {
            desiredAccess |= FileListDirectory | ReadControl;
            if (requireWriteDac)
            {
                desiredAccess |= WriteDac;
            }
        }

        var handle = CreateFile(
            volumeRoot,
            desiredAccess,
            forAuthorization ? FileShareRead : FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "Could not open the content authorization volume root without following reparse points.",
                new Win32Exception(error));
        }

        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenRelativeEntry(
        SafeFileHandle parent,
        string name,
        bool isDirectory,
        bool forAuthorization,
        bool requireWriteDac = true)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\0']) >= 0)
        {
            throw new InvalidDataException(
                "Content authorization encountered an invalid file-system entry name.");
        }

        var desiredAccess = FileReadAttributes;
        if (isDirectory)
        {
            desiredAccess |= FileListDirectory | Synchronize;
        }
        if (forAuthorization)
        {
            desiredAccess |= ReadControl;
            if (requireWriteDac)
            {
                desiredAccess |= WriteDac;
            }
        }

        using var objectName = new NativeUnicodeString(name);
        var objectAttributes = new ObjectAttributes
        {
            Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
            RootDirectory = parent.DangerousGetHandle(),
            ObjectName = objectName.Structure,
            Attributes = ObjectCaseInsensitive | ObjectDontReparse
        };
        var createOptions = FileOpenForBackupIntent | FileOpenReparsePoint
                            | (isDirectory
                                ? FileDirectoryFile | FileSynchronousIoNonAlert
                                : FileNonDirectoryFile);
        var status = NtCreateFile(
            out var nativeHandle,
            desiredAccess,
            ref objectAttributes,
            out _,
            IntPtr.Zero,
            0,
            forAuthorization ? FileShareRead : FileShareRead | FileShareWrite,
            FileOpen,
            createOptions,
            IntPtr.Zero,
            0);
        if (status < 0)
        {
            if (status == StatusReparsePointEncountered)
            {
                throw new InvalidDataException(
                    "Content authorization cannot traverse reparse points.");
            }

            var error = unchecked((int)RtlNtStatusToDosError(status));
            throw new IOException(
                $"Could not open content authorization entry '{name}' through its parent handle.",
                new Win32Exception(error));
        }

        var handle = new SafeFileHandle(nativeHandle, ownsHandle: true);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"Opening content authorization entry '{name}' returned an invalid handle.");
        }

        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static void AuthorizeDirectory(
        SafeFileHandle directory,
        SecurityIdentifier identity,
        FileSystemRights rights,
        int depth,
        ref int entryCount)
    {
        if (depth > MaximumDirectoryDepth)
        {
            throw new InvalidDataException(
                $"Content authorization cannot exceed {MaximumDirectoryDepth} directory levels.");
        }

        RegisterEntry(ref entryCount);
        foreach (var entry in EnumerateDirectory(directory))
        {
            if ((entry.Attributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Content authorization cannot traverse reparse points.");
            }

            var isDirectory = (entry.Attributes & FileAttributeDirectory) != 0;
            using var child = OpenRelativeEntry(
                directory,
                entry.Name,
                isDirectory,
                forAuthorization: true);
            var actualAttributes = ReadAttributeTag(child);
            if ((actualAttributes.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Content authorization cannot traverse reparse points.");
            }
            if (((actualAttributes.FileAttributes & FileAttributeDirectory) != 0) != isDirectory)
            {
                throw new IOException(
                    "A content authorization entry changed type while it was being opened.");
            }
            if (!isDirectory)
            {
                RejectMultipleLinks(child);
            }

            if (isDirectory)
            {
                AuthorizeDirectory(
                    child,
                    identity,
                    rights,
                    checked(depth + 1),
                    ref entryCount);
            }
            else
            {
                RegisterEntry(ref entryCount);
                GrantHandleAccess(child, identity, rights, isDirectory: false);
            }
        }

        GrantHandleAccess(directory, identity, rights, isDirectory: true);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyDirectory(
        SafeFileHandle directory,
        SecurityIdentifier identity,
        FileSystemRights rights,
        int depth,
        ref int entryCount)
    {
        if (depth > MaximumDirectoryDepth)
        {
            throw new InvalidDataException(
                $"Content authorization cannot exceed {MaximumDirectoryDepth} directory levels.");
        }

        RegisterEntry(ref entryCount);
        foreach (var entry in EnumerateDirectory(directory))
        {
            if ((entry.Attributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Content authorization cannot traverse reparse points.");
            }

            var isDirectory = (entry.Attributes & FileAttributeDirectory) != 0;
            using var child = OpenRelativeEntry(
                directory,
                entry.Name,
                isDirectory,
                forAuthorization: true,
                requireWriteDac: false);
            var actualAttributes = ReadAttributeTag(child);
            if ((actualAttributes.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Content authorization cannot traverse reparse points.");
            }
            if (((actualAttributes.FileAttributes & FileAttributeDirectory) != 0) != isDirectory)
            {
                throw new IOException(
                    "A content authorization entry changed type while it was being opened.");
            }
            if (!isDirectory)
            {
                RejectMultipleLinks(child);
                RegisterEntry(ref entryCount);
                VerifyHandleAccess(
                    child,
                    identity,
                    rights,
                    isDirectory: false,
                    requireExplicitAccess: false);
                continue;
            }

            VerifyDirectory(
                child,
                identity,
                rights,
                checked(depth + 1),
                ref entryCount);
        }

        VerifyHandleAccess(
            directory,
            identity,
            rights,
            isDirectory: true,
            requireExplicitAccess: false);
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<DirectoryEntry> EnumerateDirectory(SafeFileHandle directory)
    {
        var buffer = Marshal.AllocHGlobal(DirectoryInformationBufferSize);
        try
        {
            var informationClass = FileFullDirectoryRestartInformationClass;
            while (true)
            {
                if (!GetFileInformationByHandleEx(
                        directory,
                        informationClass,
                        buffer,
                        DirectoryInformationBufferSize))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is ErrorNoMoreFiles or ErrorHandleEof)
                    {
                        yield break;
                    }

                    throw new IOException(
                        "Could not enumerate the content authorization directory handle.",
                        new Win32Exception(error));
                }

                informationClass = FileFullDirectoryInformationClass;
                var offset = 0;
                while (true)
                {
                    if (offset < 0
                        || offset > DirectoryInformationBufferSize - FileFullDirectoryNameOffset)
                    {
                        throw new InvalidDataException(
                            "Content authorization received invalid directory information.");
                    }

                    var record = IntPtr.Add(buffer, offset);
                    var nextOffset = unchecked((uint)Marshal.ReadInt32(record));
                    var attributes = unchecked((uint)Marshal.ReadInt32(record, 56));
                    var nameLength = unchecked((uint)Marshal.ReadInt32(record, 60));
                    if ((nameLength & 1) != 0
                        || nameLength > DirectoryInformationBufferSize
                        || nameLength > DirectoryInformationBufferSize
                                         - offset
                                         - FileFullDirectoryNameOffset)
                    {
                        throw new InvalidDataException(
                            "Content authorization received an invalid directory entry name.");
                    }

                    var name = Marshal.PtrToStringUni(
                        IntPtr.Add(record, FileFullDirectoryNameOffset),
                        checked((int)nameLength / sizeof(char)))
                               ?? throw new InvalidDataException(
                                   "Content authorization received an empty directory entry name.");
                    if (name is not "." and not "..")
                    {
                        yield return new DirectoryEntry(name, attributes);
                    }

                    if (nextOffset == 0)
                    {
                        break;
                    }
                    if (nextOffset < FileFullDirectoryNameOffset
                        || nextOffset > DirectoryInformationBufferSize - offset)
                    {
                        throw new InvalidDataException(
                            "Content authorization received an invalid directory entry offset.");
                    }

                    offset = checked(offset + (int)nextOffset);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileAttributeTagInformation ReadAttributeTag(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInformationClass,
                out FileAttributeTagInformation information,
                Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw new IOException(
                "Could not verify the content authorization entry handle.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return information;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateDirectoryHandle(SafeFileHandle handle)
    {
        var information = ReadAttributeTag(handle);
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Content authorization cannot traverse reparse points.");
        }
        if ((information.FileAttributes & FileAttributeDirectory) == 0)
        {
            throw new InvalidDataException(
                "Content authorization root must be an existing canonical absolute directory.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RejectMultipleLinks(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                "Could not verify the content authorization file identity.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        if (information.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                "Content authorization cannot grant access through a multiply linked file.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void GrantHandleAccess(
        SafeFileHandle handle,
        SecurityIdentifier identity,
        FileSystemRights rights,
        bool isDirectory)
    {
        var descriptor = ReadBoundedSecurityDescriptor(handle, isDirectory);
        var discretionaryAcl = descriptor.DiscretionaryAcl
                               ?? throw new InvalidDataException(
                                   "Content authorization requires a bounded discretionary ACL.");
        ValidateAllowBounds(discretionaryAcl, identity, rights);

        discretionaryAcl.AddAccess(
            AccessControlType.Allow,
            identity,
            unchecked((int)(unchecked((uint)(int)rights) | Synchronize)),
            isDirectory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None,
            PropagationFlags.None);

        var updatedDescriptor = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(updatedDescriptor, 0);
        if (!SetKernelObjectSecurity(handle, DaclSecurityInformation, updatedDescriptor))
        {
            throw new IOException(
                "Could not apply the content authorization ACL through its stable handle.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        VerifyHandleAccess(
            handle,
            identity,
            rights,
            isDirectory,
            requireExplicitAccess: false);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyHandleAccess(
        SafeFileHandle handle,
        SecurityIdentifier identity,
        FileSystemRights rights,
        bool isDirectory,
        bool requireExplicitAccess)
    {
        var descriptor = ReadBoundedSecurityDescriptor(handle, isDirectory);
        var discretionaryAcl = descriptor.DiscretionaryAcl
                               ?? throw new InvalidDataException(
                                   "Content authorization requires a bounded discretionary ACL.");
        ValidateAllowBounds(discretionaryAcl, identity, rights);
        var blockingDenyIdentities = new HashSet<string>(StringComparer.Ordinal)
        {
            identity.Value,
            new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value,
            "S-1-15-2-1",
            "S-1-15-2-2"
        };
        var requestedAccess = ExpandFileGenericAccess(
            unchecked((uint)(int)rights) | Synchronize);
        var denied = discretionaryAcl
            .OfType<QualifiedAce>()
            .Any(ace =>
                ace.AceQualifier == AceQualifier.AccessDenied
                && (ace.AceFlags & AceFlags.InheritOnly) == 0
                && blockingDenyIdentities.Contains(ace.SecurityIdentifier.Value)
                && (ExpandFileGenericAccess(unchecked((uint)ace.AccessMask))
                    & requestedAccess) != 0);
        var allowedRules = discretionaryAcl
            .OfType<QualifiedAce>()
            .Where(ace =>
                ace.AceQualifier == AceQualifier.AccessAllowed
                && (ace.AceFlags & AceFlags.InheritOnly) == 0
                && (!requireExplicitAccess || (ace.AceFlags & AceFlags.Inherited) == 0)
                && identity.Equals(ace.SecurityIdentifier))
            .ToArray();
        var allowedAccess = allowedRules.Aggregate(
            0u,
            static (current, ace) =>
                current | ExpandFileGenericAccess(unchecked((uint)ace.AccessMask)));
        var allowed = (allowedAccess & requestedAccess) == requestedAccess;
        var inheritable = !isDirectory || discretionaryAcl
            .OfType<QualifiedAce>()
            .Any(ace =>
                ace.AceQualifier == AceQualifier.AccessAllowed
                && identity.Equals(ace.SecurityIdentifier)
                && (!requireExplicitAccess || (ace.AceFlags & AceFlags.Inherited) == 0)
                && (ace.AceFlags & AceFlags.ContainerInherit) != 0
                && (ace.AceFlags & AceFlags.ObjectInherit) != 0
                && (ExpandFileGenericAccess(unchecked((uint)ace.AccessMask))
                    & requestedAccess) == requestedAccess);
        if (denied || !allowed || !inheritable)
        {
            throw new InvalidDataException(
                "Content authorization ACL does not grant the requested effective access.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RejectInheritedBoundaryAccess(
        SafeFileHandle boundary,
        SecurityIdentifier identity)
    {
        var descriptor = ReadBoundedSecurityDescriptor(boundary, isDirectory: true);
        var inheritedAccess = descriptor.DiscretionaryAcl!
            .OfType<QualifiedAce>()
            .Any(ace =>
                ace.AceQualifier == AceQualifier.AccessAllowed
                && (ace.AceFlags & AceFlags.Inherited) != 0
                && identity.Equals(ace.SecurityIdentifier)
                && ExpandFileGenericAccess(unchecked((uint)ace.AccessMask)) != 0);
        if (inheritedAccess)
        {
            throw new InvalidDataException(
                "Content authorization boundary cannot inherit access for the target identity.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static CommonSecurityDescriptor ReadBoundedSecurityDescriptor(
        SafeFileHandle handle,
        bool isDirectory)
    {
        var descriptorBytes = ReadSecurityDescriptor(handle);
        var rawDescriptor = new RawSecurityDescriptor(descriptorBytes, offset: 0);
        if (rawDescriptor.DiscretionaryAcl is null)
        {
            throw new InvalidDataException(
                "Content authorization requires a bounded discretionary ACL and rejects a NULL DACL.");
        }

        return new CommonSecurityDescriptor(
            isContainer: isDirectory,
            isDS: false,
            descriptorBytes,
            offset: 0);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateAllowBounds(
        DiscretionaryAcl discretionaryAcl,
        SecurityIdentifier identity,
        FileSystemRights rights)
    {
        var requestedAccess = ExpandFileGenericAccess(
            unchecked((uint)(int)rights) | Synchronize);
        var broadIdentities = new HashSet<string>(StringComparer.Ordinal)
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value,
            "S-1-15-2-1",
            "S-1-15-2-2"
        };
        foreach (var ace in discretionaryAcl
                     .OfType<QualifiedAce>()
                     .Where(static ace => ace.AceQualifier == AceQualifier.AccessAllowed))
        {
            var access = ExpandFileGenericAccess(unchecked((uint)ace.AccessMask));
            if (identity.Equals(ace.SecurityIdentifier)
                && (access & ~requestedAccess) != 0)
            {
                throw new InvalidDataException(
                    "Content authorization ACL grants the target identity more than the requested least privilege.");
            }

            if (broadIdentities.Contains(ace.SecurityIdentifier.Value)
                && HasWriteOrOwnershipAccess(access))
            {
                throw new InvalidDataException(
                    "Content authorization ACL grants write or ownership access to a broad AppContainer identity.");
            }
        }
    }

    private static bool HasWriteOrOwnershipAccess(uint access) =>
        (access & (FileWriteData
                   | FileAppendData
                   | FileWriteExtendedAttributes
                   | FileDeleteChild
                   | FileWriteAttributes
                   | Delete
                   | WriteDacAccess
                   | WriteOwner)) != 0;

    private static uint ExpandFileGenericAccess(uint accessMask)
    {
        var expanded = accessMask;
        if ((accessMask & GenericRead) != 0)
        {
            expanded |= FileGenericRead;
        }
        if ((accessMask & GenericWrite) != 0)
        {
            expanded |= FileGenericWrite;
        }
        if ((accessMask & GenericExecute) != 0)
        {
            expanded |= FileGenericExecute;
        }
        if ((accessMask & GenericAll) != 0)
        {
            expanded |= FileAllAccess;
        }

        return expanded & ~(GenericRead | GenericWrite | GenericExecute | GenericAll);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ReadSecurityDescriptor(SafeFileHandle handle)
    {
        _ = GetKernelObjectSecurity(
            handle,
            DaclSecurityInformation,
            null,
            0,
            out var requiredLength);
        var error = Marshal.GetLastWin32Error();
        if (requiredLength == 0 || error != ErrorInsufficientBuffer)
        {
            throw new IOException(
                "Could not determine the content authorization security descriptor size.",
                new Win32Exception(error));
        }
        if (requiredLength > MaximumSecurityDescriptorSize)
        {
            throw new InvalidDataException(
                "Content authorization security descriptor is unexpectedly large.");
        }

        var descriptor = new byte[checked((int)requiredLength)];
        if (!GetKernelObjectSecurity(
                handle,
                DaclSecurityInformation,
                descriptor,
                requiredLength,
                out _))
        {
            throw new IOException(
                "Could not read the content authorization security descriptor.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return descriptor;
    }

    private static void RegisterEntry(ref int entryCount)
    {
        entryCount = checked(entryCount + 1);
        if (entryCount > MaximumFileSystemEntries)
        {
            throw new InvalidDataException(
                $"Content authorization cannot exceed {MaximumFileSystemEntries} file-system entries.");
        }
    }

    private sealed class NativeUnicodeString : IDisposable
    {
        private readonly IntPtr _buffer;

        public NativeUnicodeString(string value)
        {
            var byteLength = checked(value.Length * sizeof(char));
            if (byteLength > ushort.MaxValue - sizeof(char))
            {
                throw new InvalidDataException(
                    "Content authorization entry name exceeds the Windows native limit.");
            }

            _buffer = Marshal.StringToHGlobalUni(value);
            var valueStructure = new UnicodeString
            {
                Length = (ushort)byteLength,
                MaximumLength = (ushort)(byteLength + sizeof(char)),
                Buffer = _buffer
            };
            Structure = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(valueStructure, Structure, fDeleteOld: false);
        }

        public IntPtr Structure { get; }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Structure);
            Marshal.FreeHGlobal(_buffer);
        }
    }

    private readonly record struct DirectoryEntry(string Name, uint Attributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct IoStatusBlock
    {
        public readonly IntPtr Status;
        public readonly nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileAttributeTagInformation
    {
        public readonly uint FileAttributes;
        public readonly uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ByHandleFileInformation
    {
        public readonly uint FileAttributes;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public readonly uint VolumeSerialNumber;
        public readonly uint FileSizeHigh;
        public readonly uint FileSizeLow;
        public readonly uint NumberOfLinks;
        public readonly uint FileIndexHigh;
        public readonly uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeFileHandle handle,
        int requestedInformation,
        [Out] byte[]? securityDescriptor,
        uint length,
        out uint lengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        SafeFileHandle handle,
        int securityInformation,
        byte[] securityDescriptor);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
