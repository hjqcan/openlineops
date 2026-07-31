using System.Buffers;
using System.ComponentModel;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Projects.Infrastructure.Releases;

public sealed record BuildStationPackageRequest(
    string SourceDirectory,
    string PackagePath,
    string PackageId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string ProductionLineDefinitionId,
    string StationSystemId,
    string SigningKeyId,
    string SigningPrivateKeyPem,
    DateTimeOffset CreatedAtUtc);

public sealed record BuiltStationPackage(
    string PackagePath,
    StationPackageManifest Manifest);

public sealed class SignedStationPackageBuilder
{
    public const int MaximumContentEntryCount = 19_998;
    public const int MaximumSourceDirectoryCount = 20_000;
    public const long MaximumContentFileBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumContentBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;

    private const string ManifestEntryName = "package.manifest.json";
    private const string SignatureEntryName = "package.signature.json";
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private const int MaximumSignatureBytes = 64 * 1024;
    private const int MaximumDerPrivateKeyBytes = 1024 * 1024;
    private const int CopyBufferBytes = 64 * 1024;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const int ErrorHandleEof = 38;
    private const int LinuxOpenReadOnly = 0;
    private const int LinuxOpenNonBlocking = 0x00000800;
    private const int LinuxOpenDirectory = 0x00010000;
    private const int LinuxOpenNoFollow = 0x00020000;
    private const int LinuxOpenCloseOnExec = 0x00080000;
    private const uint LinuxFileTypeMask = 0xF000;
    private const uint LinuxRegularFileType = 0x8000;
    private const uint LinuxDirectoryFileType = 0x4000;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions JsonOptions =
        StationPackageCanonicalization.CreateJsonOptions();

    public static async ValueTask<BuiltStationPackage> BuildAsync(
        BuildStationPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceDirectory = ExistingDirectory(request.SourceDirectory);
        var packagePath = ResolvePackagePath(request.PackagePath, sourceDirectory);
        var createdAtUtc = RequireUtc(request.CreatedAtUtc, nameof(request.CreatedAtUtc));
        _ = StationPackageCanonicalization.CanonicalDosTimestamp(createdAtUtc);
        using SourceBoundaryLease sourceBoundary = OpenSourceBoundaryLease(
            sourceDirectory,
            ".");
        var sourceFiles = InspectSourceTree(
            sourceDirectory,
            sourceBoundary,
            cancellationToken);
        if (sourceFiles.Length == 0)
        {
            throw new InvalidDataException("A station package cannot be built from an empty directory.");
        }

        if (sourceFiles.Any(file =>
                string.Equals(file.RelativePath, ManifestEntryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(file.RelativePath, SignatureEntryName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Station package metadata paths are reserved and cannot appear in source content.");
        }


        var sensitivePath = sourceFiles.FirstOrDefault(file => IsForbiddenKeyPath(file.RelativePath));
        if (sensitivePath is not null)
        {
            throw new InvalidDataException(
                $"Station package source contains forbidden private key material '{sensitivePath.RelativePath}'.");
        }


        var projectId = Required(request.ProjectId, nameof(request.ProjectId));
        var applicationId = Required(request.ApplicationId, nameof(request.ApplicationId));
        var snapshotId = Required(request.ProjectSnapshotId, nameof(request.ProjectSnapshotId));
        var productionLineDefinitionId = Required(
            request.ProductionLineDefinitionId,
            nameof(request.ProductionLineDefinitionId));
        var stationSystemId = Required(request.StationSystemId, nameof(request.StationSystemId));
        var packageId = Required(request.PackageId, nameof(request.PackageId));
        var signingKeyId = Required(request.SigningKeyId, nameof(request.SigningKeyId));
        var signingPrivateKeyPem = RequiredPrivateKeyPem(
            request.SigningPrivateKeyPem,
            nameof(request.SigningPrivateKeyPem));

        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        var temporaryPath = packagePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            StationPackageManifest manifest;
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                var entries = new List<StationPackageEntry>(sourceFiles.Length);
                long totalContentBytes = 0;
                foreach (var source in sourceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var zipEntry = archive.CreateEntry(
                        source.RelativePath,
                        CompressionLevel.NoCompression);
                    zipEntry.LastWriteTime = createdAtUtc;
                    zipEntry.ExternalAttributes = 0;
                    await using var target = zipEntry.Open();
                    CopiedSourceFile copied = await CopySourceEntryAsync(
                             source,
                             target,
                             sourceDirectory,
                             sourceBoundary,
                            totalContentBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                    totalContentBytes = checked(totalContentBytes + copied.Length);
                    entries.Add(new StationPackageEntry(
                        source.RelativePath,
                        copied.Length,
                        copied.Sha256,
                        MediaType(source.RelativePath)));
                }

                VerifySourceTreeUnchanged(
                     sourceDirectory,
                     sourceBoundary,
                    sourceFiles,
                    cancellationToken);

                manifest = new StationPackageManifest(
                    StationPackageManifest.RequiredFormat,
                    packageId,
                    projectId,
                    applicationId,
                    snapshotId,
                    productionLineDefinitionId,
                    stationSystemId,
                    StationPackageCanonicalization.ComputeContentSha256(
                        projectId,
                        applicationId,
                        snapshotId,
                        productionLineDefinitionId,
                        stationSystemId,
                        entries),
                    createdAtUtc,
                    entries);
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                if (manifestBytes.Length > MaximumManifestBytes)
                {
                    throw new InvalidDataException(
                        "Station package manifest exceeds the canonical metadata limit.");
                }

                var signatureBytes = JsonSerializer.SerializeToUtf8Bytes(
                    Sign(manifestBytes, signingKeyId, signingPrivateKeyPem),
                    JsonOptions);
                if (signatureBytes.Length > MaximumSignatureBytes)
                {
                    throw new InvalidDataException(
                        "Station package signature exceeds the canonical metadata limit.");
                }

                await WriteEntryAsync(
                        archive,
                        ManifestEntryName,
                        manifestBytes,
                        createdAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                await WriteEntryAsync(
                        archive,
                        SignatureEntryName,
                        signatureBytes,
                        createdAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (new FileInfo(temporaryPath).Length > MaximumPackageBytes)
            {
                throw new InvalidDataException(
                    "Station package archive exceeds the canonical package size limit.");
            }

            File.Move(temporaryPath, packagePath, overwrite: false);
            return new BuiltStationPackage(packagePath, manifest);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static StationPackageSignature Sign(
        byte[] manifestBytes,
        string keyId,
        string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        if (rsa.KeySize < 3072)
        {
            throw new InvalidDataException("Station package signing RSA key must be at least 3072 bits.");
        }

        var signature = rsa.SignData(
            manifestBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
        return new StationPackageSignature(
            StationPackageSignature.RequiredAlgorithm,
            keyId,
            Convert.ToBase64String(signature));
    }

    private static async ValueTask WriteEntryAsync(
        ZipArchive archive,
        string name,
        ReadOnlyMemory<byte> content,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = createdAtUtc;
        entry.ExternalAttributes = 0;
        await using var stream = entry.Open();
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private static SourceFile[] InspectSourceTree(
        string sourceDirectory,
        SourceBoundaryLease sourceBoundary,
        CancellationToken cancellationToken)
    {
        VerifyCanonicalSourceDirectory(sourceDirectory, ".");
        VerifySourceBoundaryIdentity(sourceDirectory, sourceBoundary, ".");
        var files = new List<SourceFile>();
        var portablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(sourceDirectory);
        var directoryCount = 1;
        while (pendingDirectories.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = StationPackageCanonicalization.NormalizeRelativePath(
                    Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/'),
                    "Station package source path");
                if (!portablePaths.Add(relativePath))
                {
                    throw new InvalidDataException(
                        "Station package source contains paths that collide on a portable file system.");
                }

                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Station package source path '{relativePath}' is a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directoryCount = checked(directoryCount + 1);
                    if (directoryCount > MaximumSourceDirectoryCount)
                    {
                        throw new InvalidDataException(
                            "Station package source exceeds the canonical directory count limit.");
                    }

                    VerifyCanonicalSourceDirectory(path, relativePath);
                    pendingDirectories.Push(path);
                    continue;
                }

                if ((attributes & FileAttributes.Device) != 0)
                {
                    throw new InvalidDataException(
                        $"Station package source path '{relativePath}' is not a regular file.");
                }

                if (files.Count >= MaximumContentEntryCount)
                {
                    throw new InvalidDataException(
                        "Station package source exceeds the canonical content entry count limit.");
                }

                var source = new SourceFile(
                    Path.GetFullPath(path),
                    relativePath,
                    WindowsIdentity: null,
                    LinuxIdentity: null);
                using FileStream input = OpenSourceFile(
                    source,
                    sourceDirectory,
                    sourceBoundary);
                WindowsFileIdentity? windowsIdentity = OperatingSystem.IsWindows()
                    ? ReadRequiredSingleLinkFileIdentity(input.SafeFileHandle, relativePath)
                    : null;
                LinuxFileIdentity? linuxIdentity = OperatingSystem.IsLinux()
                    ? ReadRequiredSingleLinkLinuxFileIdentity(input.SafeFileHandle, relativePath)
                    : null;
                files.Add(source with
                {
                    WindowsIdentity = windowsIdentity,
                    LinuxIdentity = linuxIdentity
                });
            }
        }

        return [.. files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)];
    }

    private static void VerifySourceTreeUnchanged(
        string sourceDirectory,
        SourceBoundaryLease sourceBoundary,
        IReadOnlyList<SourceFile> expected,
        CancellationToken cancellationToken)
    {
        VerifySourceBoundaryIdentity(
            sourceDirectory,
            sourceBoundary,
            ".");
        SourceFile[] actual = InspectSourceTree(
            sourceDirectory,
            sourceBoundary,
            cancellationToken);
        VerifySourceBoundaryIdentity(
            sourceDirectory,
            sourceBoundary,
            ".");
        if (actual.Length != expected.Count
            || actual.Where((source, index) =>
                    !string.Equals(
                        source.RelativePath,
                        expected[index].RelativePath,
                        StringComparison.Ordinal)
                    || source.WindowsIdentity != expected[index].WindowsIdentity
                    || source.LinuxIdentity != expected[index].LinuxIdentity)
                .Any())
        {
            throw new InvalidDataException(
                "Station package source tree changed while it was packaged.");
        }
    }

    private static void VerifyCanonicalSourceDirectory(string path, string displayPath)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                $"Station package source directory '{displayPath}' is not a regular directory.");
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            _ = ReadSourceBoundaryIdentity(path, displayPath);
            return;
        }

        throw new PlatformNotSupportedException(
            "Secure Station package source traversal is supported on Windows and Linux.");
    }

    private static SourceBoundaryIdentity ReadSourceBoundaryIdentity(
        string path,
        string displayPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsSourceBoundaryIdentity(path, displayPath);
        }

        if (OperatingSystem.IsLinux())
        {
            return ReadLinuxSourceBoundaryIdentity(path, displayPath);
        }

        throw new PlatformNotSupportedException(
            "Secure Station package source traversal is supported on Windows and Linux.");
    }

    private static SourceBoundaryLease OpenSourceBoundaryLease(
        string path,
        string displayPath)
    {
        if (OperatingSystem.IsWindows())
        {
            SafeFileHandle handle = CreateFile(
                ToExtendedWindowsPath(path),
                desiredAccess: 0,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"Could not pin station package source directory '{displayPath}'.");
            }

            try
            {
                SourceBoundaryIdentity identity = ReadWindowsSourceBoundaryIdentity(
                    handle,
                    displayPath);
                VerifyCanonicalWindowsHandlePath(handle, path, displayPath);
                VerifyNoAlternateDataStreams(path, displayPath);
                return new SourceBoundaryLease(handle, identity);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            SafeFileHandle handle = OpenLinuxPath(
                path,
                LinuxOpenReadOnly
                | LinuxOpenDirectory
                | LinuxOpenNoFollow
                | LinuxOpenCloseOnExec,
                displayPath);
            try
            {
                SourceBoundaryIdentity identity = ReadLinuxSourceBoundaryIdentity(
                    handle,
                    displayPath);
                VerifyCanonicalLinuxHandlePath(handle, path, displayPath);
                return new SourceBoundaryLease(handle, identity);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        throw new PlatformNotSupportedException(
            "Secure Station package source traversal is supported on Windows and Linux.");
    }

    private static SourceBoundaryIdentity ReadPinnedSourceBoundaryIdentity(
        SourceBoundaryLease sourceBoundary,
        string displayPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsSourceBoundaryIdentity(sourceBoundary.Handle, displayPath);
        }

        if (OperatingSystem.IsLinux())
        {
            return ReadLinuxSourceBoundaryIdentity(sourceBoundary.Handle, displayPath);
        }

        throw new PlatformNotSupportedException(
            "Secure Station package source traversal is supported on Windows and Linux.");
    }

    [SupportedOSPlatform("windows")]
    private static SourceBoundaryIdentity ReadWindowsSourceBoundaryIdentity(
        string path,
        string displayPath)
    {
        using SafeFileHandle handle = CreateFile(
            ToExtendedWindowsPath(path),
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not open station package source directory '{displayPath}'.");
        }

        SourceBoundaryIdentity identity = ReadWindowsSourceBoundaryIdentity(handle, displayPath);
        VerifyCanonicalWindowsHandlePath(handle, path, displayPath);
        VerifyNoAlternateDataStreams(path, displayPath);
        return identity;
    }

    [SupportedOSPlatform("windows")]
    private static SourceBoundaryIdentity ReadWindowsSourceBoundaryIdentity(
        SafeFileHandle handle,
        string displayPath)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not inspect station package source directory '{displayPath}'.");
        }

        var handleAttributes = (FileAttributes)information.FileAttributes;
        if ((handleAttributes & FileAttributes.Directory) == 0
            || (handleAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                $"Station package source directory '{displayPath}' is not a regular directory.");
        }

        return new SourceBoundaryIdentity(
            "windows",
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    [SupportedOSPlatform("linux")]
    private static SourceBoundaryIdentity ReadLinuxSourceBoundaryIdentity(
        string path,
        string displayPath)
    {
        using SafeFileHandle handle = OpenLinuxPath(
            path,
            LinuxOpenReadOnly
            | LinuxOpenDirectory
            | LinuxOpenNoFollow
            | LinuxOpenCloseOnExec,
            displayPath);
        SourceBoundaryIdentity identity = ReadLinuxSourceBoundaryIdentity(handle, displayPath);
        VerifyCanonicalLinuxHandlePath(handle, path, displayPath);
        return identity;
    }

    [SupportedOSPlatform("linux")]
    private static SourceBoundaryIdentity ReadLinuxSourceBoundaryIdentity(
        SafeFileHandle handle,
        string displayPath)
    {
        if (LinuxFileStatus(handle, out LinuxFileStatusBuffer status) != 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not inspect station package source directory '{displayPath}'.");
        }

        if ((status.Mode & LinuxFileTypeMask) != LinuxDirectoryFileType)
        {
            throw new InvalidDataException(
                $"Station package source directory '{displayPath}' is not a regular directory.");
        }

        return new SourceBoundaryIdentity("linux", status.Device, status.Inode);
    }

    private static void VerifySourceBoundaryIdentity(
        string sourceDirectory,
        SourceBoundaryLease sourceBoundary,
        string displayPath)
    {
        if (ReadSourceBoundaryIdentity(sourceDirectory, displayPath) != sourceBoundary.Identity
            || ReadPinnedSourceBoundaryIdentity(sourceBoundary, displayPath)
            != sourceBoundary.Identity)
        {
            throw new InvalidDataException(
                "Station package source directory identity changed while it was packaged.");
        }
    }

    private static async ValueTask<CopiedSourceFile> CopySourceEntryAsync(
        SourceFile source,
        Stream target,
        string sourceDirectory,
        SourceBoundaryLease sourceBoundary,
        long priorContentBytes,
        CancellationToken cancellationToken)
    {
        VerifySourceBoundaryIdentity(
            sourceDirectory,
            sourceBoundary,
            ".");
        await using FileStream input = OpenSourceFile(
            source,
            sourceDirectory,
            sourceBoundary);
        WindowsFileIdentity? windowsIdentity = OperatingSystem.IsWindows()
            ? ReadRequiredSingleLinkFileIdentity(input.SafeFileHandle, source.RelativePath)
            : null;
        LinuxFileIdentity? linuxIdentity = OperatingSystem.IsLinux()
            ? ReadRequiredSingleLinkLinuxFileIdentity(
                input.SafeFileHandle,
                source.RelativePath)
            : null;
        if (windowsIdentity != source.WindowsIdentity
            || linuxIdentity != source.LinuxIdentity)
        {
            throw new InvalidDataException(
                $"Station package source file '{source.RelativePath}' changed while it was packaged.");
        }
        long expectedLength = windowsIdentity?.SizeBytes
                              ?? linuxIdentity?.SizeBytes
                              ?? input.Length;
        if (expectedLength < 0
            || expectedLength > MaximumContentFileBytes
            || priorContentBytes > MaximumContentBytes - expectedLength)
        {
            throw new InvalidDataException(
                $"Station package source file '{source.RelativePath}' exceeds the canonical size limit.");
        }

        var privateKeyDetector = new PrivateKeyPemDetector();
        using MemoryStream? derPrivateKeyCandidate = expectedLength <= MaximumDerPrivateKeyBytes
            ? new MemoryStream(checked((int)expectedLength))
            : null;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        long copiedLength = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(
                        buffer.AsMemory(0, CopyBufferBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                copiedLength = checked(copiedLength + read);
                if (copiedLength > MaximumContentFileBytes
                    || priorContentBytes > MaximumContentBytes - copiedLength)
                {
                    throw new InvalidDataException(
                        $"Station package source file '{source.RelativePath}' exceeds the canonical size limit.");
                }

                hash.AppendData(buffer, 0, read);
                privateKeyDetector.Append(buffer.AsSpan(0, read));
                derPrivateKeyCandidate?.Write(buffer, 0, read);
                await target.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Station package source exceeds the canonical total size limit.",
                exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (copiedLength != expectedLength
            || input.Length != expectedLength
            || WindowsSourceIdentityChanged(
                windowsIdentity,
                input.SafeFileHandle,
                source.RelativePath)
            || LinuxSourceIdentityChanged(
                linuxIdentity,
                input.SafeFileHandle,
                source.RelativePath))
        {
            throw new InvalidDataException(
                $"Station package source file '{source.RelativePath}' changed while it was packaged.");
        }

        if (privateKeyDetector.ContainsPrivateKey
            || (derPrivateKeyCandidate is not null
                && ContainsDerPrivateKey(derPrivateKeyCandidate.GetBuffer().AsSpan(
                    0,
                    checked((int)derPrivateKeyCandidate.Length)))))
        {
            throw new InvalidDataException(
                $"Station package source contains private key material '{source.RelativePath}'.");
        }

        return new CopiedSourceFile(
            copiedLength,
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static bool WindowsSourceIdentityChanged(
        WindowsFileIdentity? expected,
        SafeFileHandle fileHandle,
        string displayPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return expected != ReadRequiredSingleLinkFileIdentity(fileHandle, displayPath);
    }

    private static bool LinuxSourceIdentityChanged(
        LinuxFileIdentity? expected,
        SafeFileHandle fileHandle,
        string displayPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        return expected != ReadRequiredSingleLinkLinuxFileIdentity(fileHandle, displayPath);
    }

    private static FileStream OpenSourceFile(
        SourceFile source,
        string sourceDirectory,
        SourceBoundaryLease sourceBoundary)
    {
        if (OperatingSystem.IsLinux())
        {
            return OpenLinuxSourceFile(source, sourceDirectory, sourceBoundary);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Secure Station package source traversal is supported on Windows and Linux.");
        }

        SafeFileHandle handle = CreateFile(
            ToExtendedWindowsPath(source.AbsolutePath),
            GenericRead,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not open station package source file '{source.RelativePath}'.");
        }

        try
        {
            _ = ReadRequiredSingleLinkFileIdentity(handle, source.RelativePath);
            VerifyCanonicalWindowsHandlePath(
                handle,
                source.AbsolutePath,
                source.RelativePath);
            VerifyInsideSourceBoundary(source.AbsolutePath, sourceDirectory, source.RelativePath);
            VerifyNoAlternateDataStreams(source.AbsolutePath, source.RelativePath);
            return new FileStream(
                handle,
                FileAccess.Read,
                CopyBufferBytes,
                isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("linux")]
    private static FileStream OpenLinuxSourceFile(
        SourceFile source,
        string sourceDirectory,
        SourceBoundaryLease sourceBoundary)
    {
        var directoryHandles = new List<SafeFileHandle>();
        SafeFileHandle? fileHandle = null;
        try
        {
            SafeFileHandle rootHandle = sourceBoundary.Handle;
            if (!string.Equals(sourceBoundary.Identity.Platform, "linux", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Station package source boundary platform is inconsistent.");
            }

            VerifyCanonicalLinuxHandlePath(rootHandle, sourceDirectory, ".");

            string[] segments = source.RelativePath.Split('/');
            SafeFileHandle parent = rootHandle;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                parent = OpenLinuxPathAt(
                    parent,
                    segments[index],
                    LinuxOpenReadOnly
                    | LinuxOpenDirectory
                    | LinuxOpenNoFollow
                    | LinuxOpenCloseOnExec,
                    source.RelativePath);
                directoryHandles.Add(parent);
            }

            fileHandle = OpenLinuxPathAt(
                parent,
                segments[^1],
                LinuxOpenReadOnly
                | LinuxOpenNonBlocking
                | LinuxOpenNoFollow
                | LinuxOpenCloseOnExec,
                source.RelativePath);
            _ = ReadRequiredSingleLinkLinuxFileIdentity(
                fileHandle,
                source.RelativePath);
            VerifyCanonicalLinuxHandlePath(
                fileHandle,
                source.AbsolutePath,
                source.RelativePath);
            var stream = new FileStream(
                fileHandle,
                FileAccess.Read,
                CopyBufferBytes,
                isAsync: false);
            fileHandle = null;
            return stream;
        }
        finally
        {
            fileHandle?.Dispose();
            foreach (SafeFileHandle directoryHandle in directoryHandles)
            {
                directoryHandle.Dispose();
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static SafeFileHandle OpenLinuxPath(
        string path,
        int flags,
        string displayPath)
    {
        int descriptor = LinuxOpen(path, flags);
        return RequiredLinuxHandle(descriptor, displayPath);
    }

    [SupportedOSPlatform("linux")]
    private static SafeFileHandle OpenLinuxPathAt(
        SafeFileHandle parent,
        string path,
        int flags,
        string displayPath)
    {
        int descriptor = LinuxOpenAt(parent, path, flags);
        return RequiredLinuxHandle(descriptor, displayPath);
    }

    [SupportedOSPlatform("linux")]
    private static SafeFileHandle RequiredLinuxHandle(int descriptor, string displayPath)
    {
        if (descriptor < 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not open station package source path '{displayPath}' without following links.");
        }

        return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
    }

    [SupportedOSPlatform("linux")]
    private static LinuxFileIdentity ReadRequiredSingleLinkLinuxFileIdentity(
        SafeFileHandle fileHandle,
        string displayPath)
    {
        if (LinuxFileStatus(fileHandle, out LinuxFileStatusBuffer status) != 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not inspect station package source file '{displayPath}'.");
        }

        if ((status.Mode & LinuxFileTypeMask) != LinuxRegularFileType)
        {
            throw new InvalidDataException(
                $"Station package source path '{displayPath}' is not a regular file.");
        }

        if (status.LinkCount != 1)
        {
            throw new InvalidDataException(
                $"Station package source file '{displayPath}' must have exactly one hard link.");
        }

        if (status.SizeBytes < 0)
        {
            throw new InvalidDataException(
                $"Station package source file '{displayPath}' has an invalid size.");
        }

        return new LinuxFileIdentity(
            status.Device,
            status.Inode,
            status.LinkCount,
            status.Mode,
            status.SizeBytes,
            status.ModifiedTimeSeconds,
            status.ModifiedTimeNanoseconds,
            status.ChangedTimeSeconds,
            status.ChangedTimeNanoseconds);
    }

    [SupportedOSPlatform("linux")]
    private static void VerifyCanonicalLinuxHandlePath(
        SafeFileHandle handle,
        string expectedPath,
        string displayPath)
    {
        string descriptorPath = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}";
        FileSystemInfo? target = File.ResolveLinkTarget(descriptorPath, returnFinalTarget: true);
        if (target is null
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Station package source path '{displayPath}' resolves through a symbolic link or path alias.");
        }
    }

    private static void VerifyInsideSourceBoundary(
        string path,
        string sourceDirectory,
        string displayPath)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory))
                     + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathComparison))
        {
            throw new InvalidDataException(
                $"Station package source path '{displayPath}' escapes its source directory.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static WindowsFileIdentity ReadRequiredSingleLinkFileIdentity(
        SafeFileHandle fileHandle,
        string displayPath)
    {
        if (!GetFileInformationByHandle(fileHandle, out ByHandleFileInformation information))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not inspect station package source file '{displayPath}'.");
        }

        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & (FileAttributes.Directory
                           | FileAttributes.ReparsePoint
                           | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                $"Station package source path '{displayPath}' is not a regular file.");
        }

        if (information.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                $"Station package source file '{displayPath}' must have exactly one hard link.");
        }

        ulong size = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (size > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Station package source file '{displayPath}' is too large.");
        }

        return new WindowsFileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            information.NumberOfLinks,
            checked((long)size),
            ((ulong)(uint)information.LastWriteTime.dwHighDateTime << 32)
            | (uint)information.LastWriteTime.dwLowDateTime);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyCanonicalWindowsHandlePath(
        SafeFileHandle handle,
        string expectedPath,
        string displayPath)
    {
        var finalPath = new char[32_768];
        uint length = GetFinalPathNameByHandle(
            handle,
            finalPath,
            checked((uint)finalPath.Length),
            flags: 0);
        if (length == 0 || length >= finalPath.Length)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not resolve station package source path '{displayPath}'.");
        }

        var resolved = NormalizeFinalWindowsPath(new string(
            finalPath,
            startIndex: 0,
            checked((int)length)));
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath));
        if (!string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Station package source path '{displayPath}' resolves through a reparse point or path alias.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyNoAlternateDataStreams(string path, string displayPath)
    {
        IntPtr searchHandle = FindFirstStream(
            ToExtendedWindowsPath(path),
            StreamInfoLevels.FindStreamInfoStandard,
            out Win32FindStreamData streamData,
            reserved: 0);
        if (searchHandle == InvalidHandleValue)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorHandleEof)
            {
                return;
            }

            throw new Win32Exception(
                error,
                $"Could not enumerate data streams for station package source '{displayPath}'.");
        }

        try
        {
            while (true)
            {
                if (!string.Equals(streamData.StreamName, "::$DATA", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Station package source '{displayPath}' contains a named alternate data stream.");
                }

                if (FindNextStream(searchHandle, out streamData))
                {
                    continue;
                }

                int error = Marshal.GetLastPInvokeError();
                if (error != ErrorHandleEof)
                {
                    throw new Win32Exception(
                        error,
                        $"Could not finish enumerating data streams for station package source '{displayPath}'.");
                }

                return;
            }
        }
        finally
        {
            _ = FindClose(searchHandle);
        }
    }

    private static string NormalizeFinalWindowsPath(string value)
    {
        const string devicePrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        var path = value.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)
            ? @"\\" + value[uncPrefix.Length..]
            : value.StartsWith(devicePrefix, StringComparison.Ordinal)
                ? value[devicePrefix.Length..]
                : value;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    [SupportedOSPlatform("windows")]
    private static string ToExtendedWindowsPath(string value)
    {
        const string devicePrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        var path = Path.GetFullPath(value);
        if (path.StartsWith(devicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return uncPrefix + path[2..];
        }

        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == Path.VolumeSeparatorChar
            && path[2] == Path.DirectorySeparatorChar)
        {
            return devicePrefix + path;
        }

        throw new InvalidDataException(
            "Station package source native Windows paths must be absolute drive or UNC paths.");
    }

    private static string ExistingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return Directory.Exists(fullPath)
            ? fullPath
            : throw new DirectoryNotFoundException($"Station package source '{fullPath}' does not exist.");
    }

    private static string ResolvePackagePath(string path, string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".olopkg", StringComparison.Ordinal))
        {
            throw new ArgumentException("Station package path must use the .olopkg extension.", nameof(path));
        }

        var sourcePrefix = sourceDirectory.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (fullPath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Station package output must be outside its source directory.",
                nameof(path));
        }

        return fullPath;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException($"{parameterName} must be canonical non-empty text.", parameterName)
            : value;

    private static string RequiredPrivateKeyPem(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                $"{parameterName} must contain PEM private key material.",
                parameterName)
            : value;

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException($"{parameterName} must use UTC offset zero.", parameterName);

    private static string MediaType(string path) => Path.GetExtension(path) switch
    {
        ".json" => "application/json",
        ".py" => "text/x-python",
        ".dll" or ".exe" => "application/vnd.microsoft.portable-executable",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".csv" => "text/csv",
        ".txt" or ".log" => "text/plain",
        _ => "application/octet-stream"
    };

    private static bool IsForbiddenKeyPath(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".key", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("private-key", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("private_key", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsDerPrivateKey(ReadOnlySpan<byte> content) =>
        ImportsRsaPrivateKey(content)
        || ImportsEcPrivateKey(content)
        || ImportsDsaPrivateKey(content)
        || ImportsEcdhPrivateKey(content)
        || IsEncryptedPkcs8PrivateKey(content);

    private static bool ImportsRsaPrivateKey(ReadOnlySpan<byte> content)
    {
        using var rsa = RSA.Create();
        if (TryImportPrivateKey(
                content,
                (ReadOnlySpan<byte> bytes, out int bytesRead) =>
                    rsa.ImportPkcs8PrivateKey(bytes, out bytesRead)))
        {
            return true;
        }

        return TryImportPrivateKey(
            content,
            (ReadOnlySpan<byte> bytes, out int bytesRead) =>
                rsa.ImportRSAPrivateKey(bytes, out bytesRead));
    }

    private static bool ImportsEcPrivateKey(ReadOnlySpan<byte> content)
    {
        using var ecdsa = ECDsa.Create();
        if (TryImportPrivateKey(
                content,
                (ReadOnlySpan<byte> bytes, out int bytesRead) =>
                    ecdsa.ImportPkcs8PrivateKey(bytes, out bytesRead)))
        {
            return true;
        }

        return TryImportPrivateKey(
            content,
            (ReadOnlySpan<byte> bytes, out int bytesRead) =>
                ecdsa.ImportECPrivateKey(bytes, out bytesRead));
    }

    private static bool ImportsDsaPrivateKey(ReadOnlySpan<byte> content)
    {
        using var dsa = DSA.Create();
        return TryImportPrivateKey(
            content,
            (ReadOnlySpan<byte> bytes, out int bytesRead) =>
                dsa.ImportPkcs8PrivateKey(bytes, out bytesRead));
    }

    private static bool ImportsEcdhPrivateKey(ReadOnlySpan<byte> content)
    {
        using var ecdh = ECDiffieHellman.Create();
        return TryImportPrivateKey(
            content,
            (ReadOnlySpan<byte> bytes, out int bytesRead) =>
                ecdh.ImportPkcs8PrivateKey(bytes, out bytesRead));
    }

    private static bool TryImportPrivateKey(
        ReadOnlySpan<byte> content,
        PrivateKeyImporter importer)
    {
        try
        {
            importer(content, out int bytesRead);
            return bytesRead == content.Length;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsEncryptedPkcs8PrivateKey(ReadOnlySpan<byte> content)
    {
        try
        {
            var reader = new AsnReader(content.ToArray(), AsnEncodingRules.DER);
            AsnReader encryptedPrivateKey = reader.ReadSequence();
            AsnReader encryptionAlgorithm = encryptedPrivateKey.ReadSequence();
            string algorithmOid = encryptionAlgorithm.ReadObjectIdentifier();
            if (!encryptionAlgorithm.HasData)
            {
                return false;
            }

            _ = encryptionAlgorithm.ReadEncodedValue();
            if (encryptionAlgorithm.HasData)
            {
                return false;
            }

            byte[] encryptedData = encryptedPrivateKey.ReadOctetString();
            if (encryptedPrivateKey.HasData || reader.HasData)
            {
                return false;
            }

            return encryptedData.Length != 0
                && (algorithmOid is "1.2.840.113549.1.5.1"
                or "1.2.840.113549.1.5.3"
                or "1.2.840.113549.1.5.4"
                or "1.2.840.113549.1.5.6"
                or "1.2.840.113549.1.5.10"
                or "1.2.840.113549.1.5.11"
                or "1.2.840.113549.1.5.13"
                || algorithmOid.StartsWith("1.2.840.113549.1.12.1.", StringComparison.Ordinal));
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private sealed class PrivateKeyPemDetector
    {
        private readonly EncodedPemDetector[] _detectors =
        [
            new("-----BEGIN "u8.ToArray(), "PRIVATE KEY-----"u8.ToArray()),
            new(
                System.Text.Encoding.Unicode.GetBytes("-----BEGIN "),
                System.Text.Encoding.Unicode.GetBytes("PRIVATE KEY-----")),
            new(
                System.Text.Encoding.BigEndianUnicode.GetBytes("-----BEGIN "),
                System.Text.Encoding.BigEndianUnicode.GetBytes("PRIVATE KEY-----"))
        ];

        public bool ContainsPrivateKey => _detectors.Any(detector => detector.Matched);

        public void Append(ReadOnlySpan<byte> bytes)
        {
            foreach (EncodedPemDetector detector in _detectors)
            {
                detector.Append(bytes);
            }
        }

        private sealed class EncodedPemDetector(byte[] beginMarker, byte[] privateKeyMarker)
        {
            private int _beginMatchLength;
            private int _privateKeyMatchLength;
            private bool _sawBegin;

            public bool Matched { get; private set; }

            public void Append(ReadOnlySpan<byte> bytes)
            {
                foreach (byte value in bytes)
                {
                    if (Matched)
                    {
                        return;
                    }

                    if (!_sawBegin)
                    {
                        _beginMatchLength = NextMatchLength(
                            beginMarker,
                            _beginMatchLength,
                            value);
                        if (_beginMatchLength == beginMarker.Length)
                        {
                            _sawBegin = true;
                        }

                        continue;
                    }

                    _privateKeyMatchLength = NextMatchLength(
                        privateKeyMarker,
                        _privateKeyMatchLength,
                        value);
                    Matched = _privateKeyMatchLength == privateKeyMarker.Length;
                }
            }

            private static int NextMatchLength(
                ReadOnlySpan<byte> marker,
                int matched,
                byte value)
            {
                if (value == marker[matched])
                {
                    return matched + 1;
                }

                return value == marker[0] ? 1 : 0;
            }
        }
    }

    private sealed record CopiedSourceFile(long Length, string Sha256);

    private sealed record SourceFile(
        string AbsolutePath,
        string RelativePath,
        WindowsFileIdentity? WindowsIdentity,
        LinuxFileIdentity? LinuxIdentity);

    private sealed class SourceBoundaryLease(
        SafeFileHandle handle,
        SourceBoundaryIdentity identity) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;

        public SourceBoundaryIdentity Identity { get; } = identity;

        public void Dispose() => Handle.Dispose();
    }

    private sealed record SourceBoundaryIdentity(
        string Platform,
        ulong Device,
        ulong FileIndex);

    private sealed record WindowsFileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex,
        uint NumberOfLinks,
        long SizeBytes,
        ulong LastWriteTime);

    private sealed record LinuxFileIdentity(
        ulong Device,
        ulong Inode,
        ulong LinkCount,
        uint Mode,
        long SizeBytes,
        long ModifiedTimeSeconds,
        long ModifiedTimeNanoseconds,
        long ChangedTimeSeconds,
        long ChangedTimeNanoseconds);

    private delegate void PrivateKeyImporter(
        ReadOnlySpan<byte> source,
        out int bytesRead);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxFileStatusBuffer
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public int Padding;
        public ulong DeviceType;
        public long SizeBytes;
        public long BlockSize;
        public long BlockCount;
        public long AccessTimeSeconds;
        public long AccessTimeNanoseconds;
        public long ModifiedTimeSeconds;
        public long ModifiedTimeNanoseconds;
        public long ChangedTimeSeconds;
        public long ChangedTimeNanoseconds;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    private enum StreamInfoLevels
    {
        FindStreamInfoStandard = 0
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "FindFirstStreamW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr FindFirstStream(
        string fileName,
        StreamInfoLevels infoLevel,
        out Win32FindStreamData findStreamData,
        uint reserved);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "FindNextStreamW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(
        IntPtr findStreamHandle,
        out Win32FindStreamData findStreamData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFileHandle);

    [DllImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int LinuxOpen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport(
        "libc",
        EntryPoint = "openat",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int LinuxOpenAt(
        SafeFileHandle directoryHandle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int LinuxFileStatus(
        SafeFileHandle fileHandle,
        out LinuxFileStatusBuffer status);
}
