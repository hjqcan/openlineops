using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.ContentProtection;

internal static class WindowsHandleBoundTreeDeletion
{
    private const int MaximumDirectoryDepth = 256;
    private const int DirectoryInformationBufferSize = 64 * 1024;
    private const int MaximumStreamInformationBufferSize = 1024 * 1024;

    private const uint DeleteAccess = 0x00010000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
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
    private const uint FileDispositionFlagDelete = 0x00000001;
    private const uint FileDispositionFlagPosixSemantics = 0x00000002;
    private const uint FileDispositionFlagIgnoreReadOnlyAttribute = 0x00000010;

    private const int FileStreamInformationClass = 7;
    private const int FileAttributeTagInformationClass = 9;
    private const int FileFullDirectoryInformationClass = 14;
    private const int FileFullDirectoryRestartInformationClass = 15;
    private const int FileDispositionInformationExClass = 21;
    private const int FileFullDirectoryNameOffset = 68;
    private const int FileStreamNameOffset = 24;

    private const int ErrorNoMoreFiles = 18;
    private const int ErrorHandleEof = 38;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int StatusNoSuchFile = unchecked((int)0xc000000f);
    private const int StatusObjectNameNotFound = unchecked((int)0xc0000034);
    private const int StatusObjectPathNotFound = unchecked((int)0xc000003a);
    private const int StatusFileIsADirectory = unchecked((int)0xc00000ba);
    private const int StatusNotADirectory = unchecked((int)0xc0000103);
    private const int StatusReparsePointEncountered = unchecked((int)0xc000050b);

    [SupportedOSPlatform("windows")]
    internal static void DeleteDirectChildTree(
        string parentDirectory,
        string childName,
        int maximumEntryCount,
        long maximumBytes,
        Action<string>? afterEntryOpened = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Handle-bound tree deletion requires Windows native file-system handles.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ValidateEntryName(childName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntryCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        List<SafeFileHandle> boundaryHandles = OpenBoundary(parentDirectory);
        try
        {
            SafeFileHandle boundary = boundaryHandles[^1];
            var directEntryCount = 0;
            var matches = new List<DirectoryEntry>(capacity: 1);
            foreach (DirectoryEntry entry in EnumerateDirectory(boundary))
            {
                directEntryCount = checked(directEntryCount + 1);
                if (directEntryCount > maximumEntryCount)
                {
                    throw new InvalidDataException(
                        "Handle-bound staging cleanup parent exceeds its bounded direct entry count.");
                }
                if (string.Equals(entry.Name, childName, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(entry);
                }
            }

            if (matches.Count == 0)
            {
                return;
            }
            if (matches.Count != 1
                || !string.Equals(matches[0].Name, childName, StringComparison.Ordinal)
                || (matches[0].Attributes & FileAttributeDirectory) == 0
                || (matches[0].Attributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Handle-bound staging cleanup target has a noncanonical name or type.");
            }

            SafeFileHandle? root = OpenRelativeEntry(
                boundary,
                childName,
                isDirectory: true,
                allowMissing: true,
                forDeletion: true,
                listDirectory: true);
            if (root is null)
            {
                return;
            }

            using (root)
            {
                var entryCount = 1;
                long totalBytes = 0;
                DeleteDirectory(
                    root,
                    childName,
                    depth: 0,
                    maximumEntryCount,
                    maximumBytes,
                    ref entryCount,
                    ref totalBytes,
                    afterEntryOpened);
            }

            using SafeFileHandle? replacement = OpenRelativeEntry(
                boundary,
                childName,
                isDirectory: true,
                allowMissing: true,
                forDeletion: false,
                listDirectory: false);
            if (replacement is not null)
            {
                throw new IOException(
                    "Handle-bound staging cleanup detected a replacement at the deleted cache entry name.");
            }
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
    private static List<SafeFileHandle> OpenBoundary(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new InvalidDataException(
                "Handle-bound tree deletion requires a canonical absolute parent directory.");
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(
                canonical,
                Path.TrimEndingDirectorySeparator(directory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Handle-bound tree deletion requires a canonical absolute parent directory.");
        }

        var volumeRoot = Path.GetPathRoot(canonical)
                         ?? throw new InvalidDataException(
                             "Handle-bound tree deletion parent has no volume root.");
        var relativePath = canonical.Length == volumeRoot.Length
            ? string.Empty
            : canonical[volumeRoot.Length..];
        string[] components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component is "." or ".."))
        {
            throw new InvalidDataException(
                "Handle-bound tree deletion parent contains a noncanonical path component.");
        }

        var handles = new List<SafeFileHandle>(components.Length + 1);
        try
        {
            SafeFileHandle current = OpenVolumeRoot(volumeRoot);
            handles.Add(current);
            ValidateDirectoryHandle(current, "volume root");
            for (var index = 0; index < components.Length; index++)
            {
                current = OpenRelativeEntry(
                              current,
                              components[index],
                              isDirectory: true,
                              allowMissing: false,
                              forDeletion: false,
                              listDirectory: index == components.Length - 1)
                          ?? throw new DirectoryNotFoundException(
                              "Handle-bound tree deletion parent disappeared while it was opened.");
                handles.Add(current);
                ValidateDirectoryHandle(current, "parent boundary");
            }

            return handles;
        }
        catch
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }

            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenVolumeRoot(string volumeRoot)
    {
        SafeFileHandle handle = CreateFile(
            volumeRoot,
            FileTraverse | FileReadAttributes | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                "Could not open the handle-bound deletion volume root without following reparse points.",
                new Win32Exception(error));
        }

        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle? OpenRelativeEntry(
        SafeFileHandle parent,
        string name,
        bool isDirectory,
        bool allowMissing,
        bool forDeletion,
        bool listDirectory)
    {
        ValidateEntryName(name);
        var desiredAccess = FileReadAttributes | Synchronize;
        if (forDeletion)
        {
            desiredAccess |= DeleteAccess;
        }
        if (isDirectory)
        {
            desiredAccess |= FileTraverse;
            if (listDirectory)
            {
                desiredAccess |= FileListDirectory;
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
        var createOptions = FileOpenForBackupIntent
                            | FileOpenReparsePoint
                            | FileSynchronousIoNonAlert
                            | (isDirectory ? FileDirectoryFile : FileNonDirectoryFile);
        var status = NtCreateFile(
            out var nativeHandle,
            desiredAccess,
            ref objectAttributes,
            out _,
            IntPtr.Zero,
            0,
            forDeletion
                ? FileShareRead | FileShareWrite
                : FileShareRead | FileShareWrite | FileShareDelete,
            FileOpen,
            createOptions,
            IntPtr.Zero,
            0);
        if (status < 0)
        {
            if (allowMissing && status is StatusNoSuchFile
                or StatusObjectNameNotFound
                or StatusObjectPathNotFound)
            {
                return null;
            }

            if (status == StatusReparsePointEncountered)
            {
                throw new InvalidDataException(
                    "Handle-bound staging cleanup cannot open or traverse a reparse point.");
            }
            if (status is StatusFileIsADirectory or StatusNotADirectory)
            {
                throw new InvalidDataException(
                    $"Handle-bound staging entry '{name}' changed or has a noncanonical type.");
            }

            var error = unchecked((int)RtlNtStatusToDosError(status));
            throw new IOException(
                $"Could not open handle-bound staging entry '{name}' through its verified parent.",
                new Win32Exception(error));
        }

        var handle = new SafeFileHandle(nativeHandle, ownsHandle: true);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"Opening handle-bound staging entry '{name}' returned an invalid handle.");
        }

        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteDirectory(
        SafeFileHandle directory,
        string relativePath,
        int depth,
        int maximumEntryCount,
        long maximumBytes,
        ref int entryCount,
        ref long totalBytes,
        Action<string>? afterEntryOpened)
    {
        if (depth > MaximumDirectoryDepth)
        {
            throw new InvalidDataException(
                $"Handle-bound staging cleanup cannot exceed {MaximumDirectoryDepth} directory levels.");
        }

        FileIdentity directoryIdentity = ReadIdentity(directory, expectDirectory: true, relativePath);
        VerifyNoAlternateDataStreams(directory, relativePath);

        var entries = new List<DirectoryEntry>();
        foreach (DirectoryEntry entry in EnumerateDirectory(directory))
        {
            RegisterEntry(ref entryCount, maximumEntryCount);
            entries.Add(entry);
        }

        foreach (DirectoryEntry entry in entries)
        {
            if ((entry.Attributes & FileAttributeReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Handle-bound staging cleanup cannot traverse a reparse point.");
            }

            var isDirectory = (entry.Attributes & FileAttributeDirectory) != 0;
            using SafeFileHandle? child = OpenRelativeEntry(
                directory,
                entry.Name,
                isDirectory,
                allowMissing: true,
                forDeletion: true,
                listDirectory: isDirectory);
            if (child is null)
            {
                continue;
            }

            var childPath = relativePath + "/" + entry.Name;
            FileIdentity childIdentity = ReadIdentity(child, isDirectory, childPath);
            VerifyNoAlternateDataStreams(child, childPath);
            if (!isDirectory)
            {
                totalBytes = AddBoundedBytes(totalBytes, childIdentity.SizeBytes, maximumBytes);
            }

            afterEntryOpened?.Invoke(childPath);
            if (isDirectory)
            {
                DeleteDirectory(
                    child,
                    childPath,
                    checked(depth + 1),
                    maximumEntryCount,
                    maximumBytes,
                    ref entryCount,
                    ref totalBytes,
                    afterEntryOpened);
                continue;
            }

            FileIdentity childIdentityAfter = ReadIdentity(child, isDirectory, childPath);
            if (childIdentity != childIdentityAfter)
            {
                throw new InvalidDataException(
                    "A handle-bound staging cleanup entry changed identity before deletion.");
            }

            MarkDelete(child, childPath);
        }

        FileIdentity directoryIdentityAfter = ReadIdentity(
            directory,
            expectDirectory: true,
            relativePath);
        if (!directoryIdentity.SameObjectAs(directoryIdentityAfter))
        {
            throw new InvalidDataException(
                "A handle-bound staging cleanup directory changed identity before deletion.");
        }

        MarkDelete(directory, relativePath);
    }

    private static void RegisterEntry(ref int entryCount, int maximumEntryCount)
    {
        entryCount = checked(entryCount + 1);
        if (entryCount > maximumEntryCount)
        {
            throw new InvalidDataException(
                "Handle-bound staging cleanup exceeds its bounded entry count.");
        }
    }

    private static long AddBoundedBytes(long current, long additional, long maximumBytes)
    {
        long total;
        try
        {
            total = checked(current + additional);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Handle-bound staging cleanup exceeds its bounded byte size.",
                exception);
        }

        if (additional < 0 || total > maximumBytes)
        {
            throw new InvalidDataException(
                "Handle-bound staging cleanup exceeds its bounded byte size.");
        }

        return total;
    }

    [SupportedOSPlatform("windows")]
    private static FileIdentity ReadIdentity(
        SafeFileHandle handle,
        bool expectDirectory,
        string displayPath)
    {
        FileAttributeTagInformation attributeTag = ReadAttributeTag(handle);
        if ((attributeTag.FileAttributes & (FileAttributeReparsePoint | FileAttributeDevice)) != 0)
        {
            throw new InvalidDataException(
                $"Handle-bound staging cleanup entry '{displayPath}' is a reparse point.");
        }

        var isDirectory = (attributeTag.FileAttributes & FileAttributeDirectory) != 0;
        if (isDirectory != expectDirectory
            || !GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Could not verify handle-bound staging entry '{displayPath}'.",
                new Win32Exception(error));
        }

        if (!expectDirectory && information.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                $"Handle-bound staging file '{displayPath}' must have exactly one hard link.");
        }

        var size = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (size > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Handle-bound staging file '{displayPath}' is too large.");
        }

        return new FileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            information.NumberOfLinks,
            expectDirectory ? 0 : checked((long)size));
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
                "Could not read handle-bound staging entry attributes.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return information;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateDirectoryHandle(SafeFileHandle handle, string boundary)
    {
        FileAttributeTagInformation information = ReadAttributeTag(handle);
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0
            || (information.FileAttributes & FileAttributeDirectory) == 0)
        {
            throw new InvalidDataException(
                $"Handle-bound staging cleanup {boundary} is not a regular directory.");
        }
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
                    var error = Marshal.GetLastPInvokeError();
                    if (error is ErrorNoMoreFiles or ErrorHandleEof)
                    {
                        yield break;
                    }

                    throw new IOException(
                        "Could not enumerate a handle-bound staging directory.",
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
                            "Handle-bound staging cleanup received invalid directory information.");
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
                            "Handle-bound staging cleanup received an invalid entry name.");
                    }

                    var name = Marshal.PtrToStringUni(
                        IntPtr.Add(record, FileFullDirectoryNameOffset),
                        checked((int)nameLength / sizeof(char)))
                               ?? throw new InvalidDataException(
                                   "Handle-bound staging cleanup received an empty entry name.");
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
                            "Handle-bound staging cleanup received an invalid entry offset.");
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
    private static void VerifyNoAlternateDataStreams(
        SafeFileHandle handle,
        string displayPath)
    {
        var bufferSize = 4096;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (!GetFileInformationByHandleEx(
                        handle,
                        FileStreamInformationClass,
                        buffer,
                        bufferSize))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorHandleEof)
                    {
                        return;
                    }
                    if (error is ErrorInsufficientBuffer or ErrorMoreData
                        && bufferSize < MaximumStreamInformationBufferSize)
                    {
                        bufferSize = Math.Min(
                            checked(bufferSize * 2),
                            MaximumStreamInformationBufferSize);
                        continue;
                    }

                    throw new IOException(
                        $"Could not enumerate data streams for handle-bound staging entry '{displayPath}'.",
                        new Win32Exception(error));
                }

                ParseStreamInformation(buffer, bufferSize, displayPath);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static void ParseStreamInformation(
        IntPtr buffer,
        int bufferSize,
        string displayPath)
    {
        var offset = 0;
        while (true)
        {
            if (offset < 0 || offset > bufferSize - FileStreamNameOffset)
            {
                throw new InvalidDataException(
                    "Handle-bound staging cleanup received invalid stream information.");
            }

            var record = IntPtr.Add(buffer, offset);
            var nextOffset = unchecked((uint)Marshal.ReadInt32(record));
            var nameLength = unchecked((uint)Marshal.ReadInt32(record, 4));
            if ((nameLength & 1) != 0
                || nameLength > bufferSize
                || nameLength > bufferSize - offset - FileStreamNameOffset)
            {
                throw new InvalidDataException(
                    "Handle-bound staging cleanup received an invalid stream name.");
            }

            var streamName = Marshal.PtrToStringUni(
                IntPtr.Add(record, FileStreamNameOffset),
                checked((int)nameLength / sizeof(char))) ?? string.Empty;
            if (!string.Equals(streamName, "::$DATA", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Handle-bound staging entry '{displayPath}' contains a named alternate data stream.");
            }

            if (nextOffset == 0)
            {
                return;
            }
            if (nextOffset < FileStreamNameOffset || nextOffset > bufferSize - offset)
            {
                throw new InvalidDataException(
                    "Handle-bound staging cleanup received an invalid stream offset.");
            }

            offset = checked(offset + (int)nextOffset);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void MarkDelete(SafeFileHandle handle, string displayPath)
    {
        var disposition = new FileDispositionInformationEx
        {
            Flags = FileDispositionFlagDelete
                    | FileDispositionFlagPosixSemantics
                    | FileDispositionFlagIgnoreReadOnlyAttribute
        };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInformationExClass,
                ref disposition,
                Marshal.SizeOf<FileDispositionInformationEx>()))
        {
            throw new IOException(
                $"Could not delete handle-bound staging entry '{displayPath}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\0']) >= 0)
        {
            throw new InvalidDataException(
                "Handle-bound staging cleanup encountered an invalid entry name.");
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
                    "Handle-bound staging cleanup entry name exceeds the Windows native limit.");
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

    private readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex,
        uint NumberOfLinks,
        long SizeBytes)
    {
        public bool SameObjectAs(FileIdentity other) =>
            VolumeSerialNumber == other.VolumeSerialNumber
            && FileIndex == other.FileIndex;
    }

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
    private struct FileDispositionInformationEx
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformationEx fileInformation,
        int bufferSize);

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
