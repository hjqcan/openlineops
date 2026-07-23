using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.Agent.Infrastructure.Packages;

public sealed record StationPackageTrustOptions(
    string ContentCacheDirectory,
    IReadOnlyDictionary<string, string> TrustedPublicKeys,
    long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024,
    int MaximumEntryCount = 20_000,
    string? ImmutableReaderSid = null,
    string? ImmutableStationServiceSid = null,
    long MaximumPackageBytes = 4L * 1024 * 1024 * 1024);

public sealed record InstalledStationPackage(
    string ContentDirectory,
    StationPackageManifest Manifest);

public sealed class SignedStationPackageInstaller : IDisposable
{
    private const string ManifestEntryName = "package.manifest.json";
    private const string SignatureEntryName = "package.signature.json";
    private const string CommitEntryName = "content.sha256";
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private const int MaximumSignatureBytes = 64 * 1024;
    private const int ExpandedContentCopyBufferBytes = 64 * 1024;
    private const int EndOfCentralDirectoryBytes = 22;
    private const int Zip64EndOfCentralDirectoryBytes = 56;
    private const int Zip64EndOfCentralDirectoryLocatorBytes = 20;
    private const int CentralDirectoryHeaderBytes = 46;
    private const int LocalFileHeaderBytes = 30;
    private const int MaximumCanonicalEntryNameBytes =
        StationPackageCanonicalization.MaximumRelativePathUtf8Bytes;
    private const int MaximumCanonicalCentralExtraBytes = 28;
    private const ushort CanonicalUtf8Flag = 1 << 11;
    private const ushort CanonicalCompressionMethod = 0;
    private const int MaximumRecoverableStagingDirectories = 64;
    private const int MaximumCacheEntries = 8192;

    private readonly string _cacheDirectory;
    private readonly string _cacheIdentity;
    private readonly Dictionary<string, string> _trustedPublicKeys;
    private readonly long _maximumPackageBytes;
    private readonly long _maximumExpandedBytes;
    private readonly int _maximumEntryCount;
    private readonly IImmutableContentProtector _contentProtector;
    private readonly ImmutableContentProtectionPolicy _contentProtectionPolicy;
    private readonly SemaphoreSlim _installationGate = new(1, 1);
    private readonly ImmutableContentCacheTransactionLock _cacheTransactionLock;

    public SignedStationPackageInstaller(
        StationPackageTrustOptions options,
        IImmutableContentProtector? contentProtector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContentCacheDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumPackageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumExpandedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumEntryCount);
        if (options.TrustedPublicKeys.Count == 0)
        {
            throw new ArgumentException(
                "At least one trusted station package signing key is required.",
                nameof(options));
        }

        _cacheDirectory = Path.GetFullPath(options.ContentCacheDirectory);
        _trustedPublicKeys = new Dictionary<string, string>(
            options.TrustedPublicKeys,
            StringComparer.Ordinal);
        foreach ((var keyId, var publicKeyPem) in _trustedPublicKeys)
        {
            _ = Required(keyId, nameof(options.TrustedPublicKeys));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(Required(publicKeyPem, nameof(options.TrustedPublicKeys)));
            if (rsa.KeySize < 3072)
            {
                throw new InvalidDataException(
                    $"Trusted Station package RSA public key '{keyId}' must be at least 3072 bits.");
            }

            try
            {
                _ = rsa.ExportParameters(includePrivateParameters: true);
                throw new InvalidDataException(
                    $"Trusted Station package key '{keyId}' must contain public key material only.");
            }
            catch (CryptographicException)
            {
            }
        }

        _maximumPackageBytes = options.MaximumPackageBytes;
        _maximumExpandedBytes = options.MaximumExpandedBytes;
        _maximumEntryCount = options.MaximumEntryCount;
        _contentProtector = contentProtector ?? new ImmutableContentProtector();
        _contentProtectionPolicy = new ImmutableContentProtectionPolicy(
            ResolveImmutableReaderSid(options.ImmutableReaderSid),
            ResolveImmutableStationServiceSid(options.ImmutableStationServiceSid));
        _contentProtector.ProtectCacheBoundary(
            _cacheDirectory,
            _contentProtectionPolicy);
        _cacheIdentity = ImmutableContentProtector.GetStableDirectoryIdentity(
            _cacheDirectory);
        _cacheTransactionLock = new ImmutableContentCacheTransactionLock(
            _cacheDirectory,
            _contentProtectionPolicy.StationServiceSid);
    }

    public async ValueTask<InstalledStationPackage> InstallAsync(
        string packagePath,
        string expectedContentSha256,
        CancellationToken cancellationToken = default)
    {
        _contentProtector.VerifyCacheBoundary(
            _cacheDirectory,
            _contentProtectionPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var fullPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPackagePath))
        {
            throw new FileNotFoundException("Station package does not exist.", fullPackagePath);
        }

        var expectedHash = RequireSha256(expectedContentSha256, nameof(expectedContentSha256));
        await using var packageStream = new FileStream(
            fullPackagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        long packageLength = packageStream.Length;
        if (packageLength <= 0 || packageLength > _maximumPackageBytes)
        {
            throw new InvalidDataException(
                "Station package archive size exceeds the configured limit.");
        }

        Dictionary<string, IndexedArchiveEntry> archiveEntries = IndexEntries(
            packageStream.SafeFileHandle,
            packageLength,
            cancellationToken);
        var manifestBytes = await ReadMetadataAsync(
                RequiredEntry(archiveEntries, ManifestEntryName),
                MaximumManifestBytes,
                _maximumExpandedBytes,
                cancellationToken)
            .ConfigureAwait(false);
        long expandedBytes = manifestBytes.LongLength;
        var signatureBytes = await ReadMetadataAsync(
                RequiredEntry(archiveEntries, SignatureEntryName),
                MaximumSignatureBytes,
                _maximumExpandedBytes - expandedBytes,
                cancellationToken)
            .ConfigureAwait(false);
        expandedBytes = checked(expandedBytes + signatureBytes.LongLength);
        StationPackageManifest manifest = DeserializeCanonical<StationPackageManifest>(manifestBytes, ManifestEntryName);
        StationPackageSignature signature = DeserializeCanonical<StationPackageSignature>(signatureBytes, SignatureEntryName);
        ValidateManifest(manifest, expectedHash, expandedBytes);
        VerifySignature(manifestBytes, signature);
        ValidateArchiveIndex(archiveEntries, manifest);

        var contentDirectory = Path.Combine(_cacheDirectory, manifest.ContentSha256);
        var commitDirectory = Path.Combine(
            _cacheDirectory,
            $".{manifest.ContentSha256}.installed");
        ImmutableContentFile[] immutableInventory = [.. manifest.Entries
            .Select(entry => new ImmutableContentFile(
                entry.Path,
                entry.Length,
                entry.Sha256))];

        await _installationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VerifyPinnedCacheIdentity();
            VerifyPinnedPackageLength(packageStream.SafeFileHandle, packageLength);
            await using ImmutableContentCacheTransactionLease contentLock =
                await _cacheTransactionLock.AcquireAsync(cancellationToken)
                    .ConfigureAwait(false);
            _contentProtector.VerifyCacheBoundary(
                _cacheDirectory,
                _contentProtectionPolicy);
            VerifyPinnedCacheIdentity();
            VerifyPinnedPackageLength(packageStream.SafeFileHandle, packageLength);
            await ScavengeCacheTransactionsAsync(cancellationToken).ConfigureAwait(false);
            InstalledStationPackage installed = await InstallValidatedContentAsync(
                    archiveEntries,
                    manifest,
                    contentDirectory,
                    commitDirectory,
                    immutableInventory,
                    expandedBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            _contentProtector.VerifyCacheBoundary(
                _cacheDirectory,
                _contentProtectionPolicy);
            VerifyPinnedCacheIdentity();
            VerifyPinnedPackageLength(packageStream.SafeFileHandle, packageLength);
            return installed;
        }
        finally
        {
            _installationGate.Release();
        }
    }

    private static void VerifyPinnedPackageLength(
        SafeFileHandle packageHandle,
        long expectedLength)
    {
        if (RandomAccess.GetLength(packageHandle) != expectedLength)
        {
            throw new InvalidDataException(
                "Station package archive length changed during installation.");
        }
    }

    private void VerifyPinnedCacheIdentity()
    {
        var currentIdentity = ImmutableContentProtector.GetStableDirectoryIdentity(
            _cacheDirectory);
        if (!string.Equals(currentIdentity, _cacheIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station package cache identity changed during the installer lifetime; restart is required.");
        }
    }

    private async ValueTask ScavengeCacheTransactionsAsync(
        CancellationToken cancellationToken)
    {
        var cacheEntries = Directory
            .EnumerateFileSystemEntries(
                _cacheDirectory,
                "*",
                SearchOption.TopDirectoryOnly)
            .Take(MaximumCacheEntries + 1)
            .ToArray();
        if (cacheEntries.Length > MaximumCacheEntries)
        {
            throw new InvalidDataException(
                $"Station package cache is RecoveryRequired because it exceeds {MaximumCacheEntries} direct entries.");
        }

        var contentHashes = new HashSet<string>(StringComparer.Ordinal);
        var commitHashes = new HashSet<string>(StringComparer.Ordinal);
        var stagingDirectories = new List<string>();
        foreach (var entry in cacheEntries)
        {
            var leaf = Path.GetFileName(entry);
            if (!Directory.Exists(entry)
                || File.Exists(entry)
                || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Station package cache is RecoveryRequired because entry '{leaf}' is not a regular directory.");
            }

            if (IsSha256(leaf))
            {
                _ = contentHashes.Add(leaf);
                continue;
            }

            if (TryReadCommitHash(leaf, out var commitHash))
            {
                _ = commitHashes.Add(commitHash);
                continue;
            }

            if (IsRecoverableStagingDirectory(leaf))
            {
                stagingDirectories.Add(entry);
                continue;
            }

            throw new InvalidDataException(
                $"Station package cache is RecoveryRequired because entry '{leaf}' is not a canonical transaction state.");
        }

        string[] orphanedCommits = [.. commitHashes.Except(contentHashes, StringComparer.Ordinal)];
        if (orphanedCommits.Length != 0)
        {
            throw new InvalidDataException(
                $"Station package cache is RecoveryRequired because commit '{orphanedCommits[0]}' has no content.");
        }

        if (stagingDirectories.Count > MaximumRecoverableStagingDirectories)
        {
            throw new InvalidDataException(
                "Station package cache is RecoveryRequired because it exceeds "
                + $"{MaximumRecoverableStagingDirectories} bounded staging directories.");
        }

        string[] orderedCommitHashes = [.. commitHashes.Order(StringComparer.Ordinal)];
        foreach (var commitHash in orderedCommitHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableContentFile[] commitInventory = CreateCommitInventory(
                commitHash,
                out _);
            await _contentProtector.VerifyInventoryAsync(
                    Path.Combine(_cacheDirectory, $".{commitHash}.installed"),
                    commitInventory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var commitHash in orderedCommitHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableContentFile[] commitInventory = CreateCommitInventory(
                commitHash,
                out _);
            var commitDirectory = Path.Combine(
                _cacheDirectory,
                $".{commitHash}.installed");
            await _contentProtector.ProtectAsync(
                    commitDirectory,
                    commitInventory,
                    _contentProtectionPolicy,
                    cancellationToken)
                .ConfigureAwait(false);
            await _contentProtector.VerifyAsync(
                    commitDirectory,
                    commitInventory,
                    _contentProtectionPolicy,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var stagingDirectory in stagingDirectories)
        {
            try
            {
                ImmutableContentProtector.DeleteUnprotectedStagingDirectory(
                    _cacheDirectory,
                    stagingDirectory);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
            {
                throw new InvalidDataException(
                    "Station package cache is RecoveryRequired because an abandoned "
                    + "staging directory could not be proven safe for cleanup.",
                    exception);
            }
        }
    }

    private static bool IsRecoverableStagingDirectory(string leaf)
    {
        var segments = leaf.Split('.');
        return segments.Length == 4
               && segments[0].Length == 0
               && IsSha256(segments[1])
               && Guid.TryParseExact(segments[2], "N", out Guid transactionId)
               && string.Equals(
                   segments[2],
                   transactionId.ToString("N"),
                   StringComparison.Ordinal)
               && segments[3] is "installing" or "committing";
    }

    private static bool TryReadCommitHash(string leaf, out string contentSha256)
    {
        var segments = leaf.Split('.');
        if (segments.Length == 3
            && segments[0].Length == 0
            && IsSha256(segments[1])
            && string.Equals(segments[2], "installed", StringComparison.Ordinal))
        {
            contentSha256 = segments[1];
            return true;
        }

        contentSha256 = string.Empty;
        return false;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async ValueTask<InstalledStationPackage> InstallValidatedContentAsync(
        Dictionary<string, IndexedArchiveEntry> archiveEntries,
        StationPackageManifest manifest,
        string contentDirectory,
        string commitDirectory,
        IReadOnlyCollection<ImmutableContentFile> immutableInventory,
        long expandedBytes,
        CancellationToken cancellationToken)
    {
        if (File.Exists(commitDirectory))
        {
            throw new InvalidDataException(
                "Station package installation commit path must be a directory.");
        }

        if (Directory.Exists(commitDirectory) && !Directory.Exists(contentDirectory))
        {
            throw new InvalidDataException(
                "Station package commit exists without its content directory.");
        }

        if (Directory.Exists(contentDirectory))
        {
            await CompleteInstallationAsync(
                    manifest,
                    contentDirectory,
                    commitDirectory,
                    immutableInventory,
                    cancellationToken)
                .ConfigureAwait(false);
            return new InstalledStationPackage(contentDirectory, manifest);
        }

        var stagingDirectory = Path.Combine(
            _cacheDirectory,
            $".{manifest.ContentSha256}.{Guid.NewGuid():N}.installing");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            foreach (StationPackageEntry declared in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexedArchiveEntry zipEntry = archiveEntries[declared.Path];
                if (zipEntry.Length != declared.Length)
                {
                    throw new InvalidDataException(
                        $"Station package entry '{declared.Path}' length does not match its manifest.");
                }

                var outputPath = StationPackagePath.ResolveInside(stagingDirectory, declared.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using (var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    long remainingExpandedBytes = _maximumExpandedBytes - expandedBytes;
                    long copiedBytes = await CopyExpandedEntryAsync(
                            zipEntry,
                            output,
                            declared.Length,
                            remainingExpandedBytes,
                            declared.Path,
                            cancellationToken)
                        .ConfigureAwait(false);
                    expandedBytes = checked(expandedBytes + copiedBytes);
                }

                await VerifyFileAsync(outputPath, declared, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                Directory.Move(stagingDirectory, contentDirectory);
            }
            catch (IOException) when (Directory.Exists(contentDirectory))
            {
                ImmutableContentProtector.DeleteUnprotectedStagingDirectory(
                    _cacheDirectory,
                    stagingDirectory);
            }

            await CompleteInstallationAsync(
                    manifest,
                    contentDirectory,
                    commitDirectory,
                    immutableInventory,
                    cancellationToken)
                .ConfigureAwait(false);

            return new InstalledStationPackage(contentDirectory, manifest);
        }
        catch
        {
            ImmutableContentProtector.DeleteUnprotectedStagingDirectory(
                _cacheDirectory,
                stagingDirectory);
            throw;
        }
    }

    private async ValueTask CompleteInstallationAsync(
        StationPackageManifest manifest,
        string contentDirectory,
        string commitDirectory,
        IReadOnlyCollection<ImmutableContentFile> immutableInventory,
        CancellationToken cancellationToken)
    {
        await _contentProtector.ProtectAsync(
                contentDirectory,
                immutableInventory,
                _contentProtectionPolicy,
                cancellationToken)
            .ConfigureAwait(false);
        ImmutableContentFile[] commitInventory = CreateCommitInventory(
            manifest.ContentSha256,
            out var commitBytes);
        await ProtectCommitDirectoryAsync(
                manifest.ContentSha256,
                commitDirectory,
                commitInventory,
                commitBytes,
                cancellationToken)
            .ConfigureAwait(false);
        await _contentProtector.VerifyAsync(
                contentDirectory,
                immutableInventory,
                _contentProtectionPolicy,
                cancellationToken)
            .ConfigureAwait(false);
        await _contentProtector.VerifyAsync(
                commitDirectory,
                commitInventory,
                _contentProtectionPolicy,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask ProtectCommitDirectoryAsync(
        string contentSha256,
        string commitDirectory,
        IReadOnlyCollection<ImmutableContentFile> commitInventory,
        byte[] commitBytes,
        CancellationToken cancellationToken)
    {
        if (File.Exists(commitDirectory))
        {
            throw new InvalidDataException(
                "Station package installation commit path must be a directory.");
        }

        if (!Directory.Exists(commitDirectory))
        {
            var stagingCommitDirectory = Path.Combine(
                _cacheDirectory,
                $".{contentSha256}.{Guid.NewGuid():N}.committing");
            Directory.CreateDirectory(stagingCommitDirectory);
            try
            {
                var commitPath = Path.Combine(stagingCommitDirectory, CommitEntryName);
                await using (var output = new FileStream(
                                 commitPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(commitBytes, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    Directory.Move(stagingCommitDirectory, commitDirectory);
                }
                catch (IOException) when (Directory.Exists(commitDirectory))
                {
                    ImmutableContentProtector.DeleteUnprotectedStagingDirectory(
                        _cacheDirectory,
                        stagingCommitDirectory);
                }
            }
            catch
            {
                ImmutableContentProtector.DeleteUnprotectedStagingDirectory(
                    _cacheDirectory,
                    stagingCommitDirectory);
                throw;
            }
        }

        await _contentProtector.ProtectAsync(
                commitDirectory,
                commitInventory,
                _contentProtectionPolicy,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ImmutableContentFile[] CreateCommitInventory(
        string contentSha256,
        out byte[] commitBytes)
    {
        commitBytes = Encoding.ASCII.GetBytes(contentSha256 + "\n");
        return
        [
            new ImmutableContentFile(
                CommitEntryName,
                commitBytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(commitBytes)))
        ];
    }

    public void Dispose()
    {
        _cacheTransactionLock.Dispose();
        _installationGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private Dictionary<string, IndexedArchiveEntry> IndexEntries(
        SafeFileHandle packageHandle,
        long packageLength,
        CancellationToken cancellationToken)
    {
        ArchiveFooter footer = ReadArchiveFooter(packageHandle, packageLength);
        ulong maximumEntryCount = (ulong)(uint)_maximumEntryCount + 2UL;
        if (footer.EntryCount < 3 || footer.EntryCount > maximumEntryCount)
        {
            throw new InvalidDataException("Station package contains too many or too few entries.");
        }

        ulong minimumCentralBytes = checked(footer.EntryCount * CentralDirectoryHeaderBytes);
        ulong maximumCentralBytes = checked(
            footer.EntryCount
            * (CentralDirectoryHeaderBytes
               + MaximumCanonicalEntryNameBytes
               + MaximumCanonicalCentralExtraBytes));
        if ((ulong)footer.CentralSize < minimumCentralBytes
            || (ulong)footer.CentralSize > maximumCentralBytes)
        {
            throw new InvalidDataException(
                "Station package central directory size is outside the canonical bound.");
        }

        var centralEntries = new List<CentralArchiveEntry>(checked((int)footer.EntryCount));
        long centralCursor = footer.CentralOffset;
        long centralEnd = checked(footer.CentralOffset + footer.CentralSize);
        for (ulong index = 0; index < footer.EntryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CentralArchiveEntry entry = ReadCentralArchiveEntry(
                packageHandle,
                centralCursor,
                centralEnd);
            centralEntries.Add(entry);
            centralCursor = entry.RecordEndOffset;
        }

        if (centralCursor != centralEnd)
        {
            throw new InvalidDataException(
                "Station package central directory does not have a unique canonical boundary.");
        }

        ValidateCanonicalArchiveOrder(centralEntries);
        var result = new Dictionary<string, IndexedArchiveEntry>(
            centralEntries.Count,
            StringComparer.Ordinal);
        var caseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long localCursor = 0;
        foreach (CentralArchiveEntry centralEntry in centralEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (centralEntry.LocalHeaderOffset != localCursor)
            {
                throw new InvalidDataException(
                    $"Station package entry '{centralEntry.FullName}' central local-header offset is not canonical.");
            }

            IndexedArchiveEntry indexed = ReadLocalArchiveEntry(
                packageHandle,
                centralEntry,
                footer.CentralOffset);
            if (!result.TryAdd(indexed.FullName, indexed)
                || !caseInsensitive.Add(indexed.FullName))
            {
                throw new InvalidDataException(
                    $"Station package contains duplicate path '{indexed.FullName}'.");
            }

            localCursor = indexed.EndOffset;
        }

        if (localCursor != footer.CentralOffset)
        {
            throw new InvalidDataException(
                "Station package local entries do not end at the unique central directory boundary.");
        }

        VerifyPinnedPackageLength(packageHandle, packageLength);
        return result;
    }

    private static ArchiveFooter ReadArchiveFooter(
        SafeFileHandle packageHandle,
        long packageLength)
    {
        const uint endOfCentralDirectorySignature = 0x06054b50;
        const uint zip64EndOfCentralDirectorySignature = 0x06064b50;
        const uint zip64LocatorSignature = 0x07064b50;
        if (packageLength < EndOfCentralDirectoryBytes)
        {
            throw new InvalidDataException("Station package ZIP is truncated before its footer.");
        }

        long eocdOffset = packageLength - EndOfCentralDirectoryBytes;
        Span<byte> eocd = stackalloc byte[EndOfCentralDirectoryBytes];
        ReadArchiveBytes(packageHandle, eocdOffset, eocd, "end of central directory");
        if (BinaryPrimitives.ReadUInt32LittleEndian(eocd) != endOfCentralDirectorySignature
            || BinaryPrimitives.ReadUInt16LittleEndian(eocd[20..]) != 0)
        {
            throw new InvalidDataException(
                "Station package must end with one canonical comment-free ZIP footer.");
        }

        ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..]);
        ushort centralDiskNumber = BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..]);
        ushort entriesOnDisk16 = BinaryPrimitives.ReadUInt16LittleEndian(eocd[8..]);
        ushort entryCount16 = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        uint centralSize32 = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        uint centralOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        if (diskNumber != 0
            || centralDiskNumber != 0
            || entriesOnDisk16 != entryCount16)
        {
            throw new InvalidDataException("Multi-disk station package ZIP files are not supported.");
        }

        bool requiresZip64 = entryCount16 == ushort.MaxValue
                             || centralSize32 == uint.MaxValue
                             || centralOffset32 == uint.MaxValue;
        if (!requiresZip64)
        {
            long centralOffset = centralOffset32;
            long centralSize = centralSize32;
            if (centralOffset > eocdOffset || centralSize != eocdOffset - centralOffset)
            {
                throw new InvalidDataException(
                    "Station package central directory and ZIP footer are not contiguous.");
            }

            return new ArchiveFooter(
                entryCount16,
                centralOffset,
                centralSize);
        }

        if (eocdOffset < Zip64EndOfCentralDirectoryLocatorBytes
            + Zip64EndOfCentralDirectoryBytes)
        {
            throw new InvalidDataException("Station package ZIP64 footer is truncated.");
        }

        long locatorOffset = eocdOffset - Zip64EndOfCentralDirectoryLocatorBytes;
        Span<byte> locator = stackalloc byte[Zip64EndOfCentralDirectoryLocatorBytes];
        ReadArchiveBytes(packageHandle, locatorOffset, locator, "ZIP64 locator");
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != zip64LocatorSignature
            || BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1)
        {
            throw new InvalidDataException("Station package ZIP64 locator is not canonical.");
        }

        ulong zip64Offset64 = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
        if (zip64Offset64 > long.MaxValue)
        {
            throw new InvalidDataException("Station package ZIP64 footer offset is unsupported.");
        }

        long zip64Offset = (long)zip64Offset64;
        if (zip64Offset > locatorOffset
            || locatorOffset - zip64Offset != Zip64EndOfCentralDirectoryBytes)
        {
            throw new InvalidDataException(
                "Station package ZIP64 records are not uniquely contiguous.");
        }

        Span<byte> zip64 = stackalloc byte[Zip64EndOfCentralDirectoryBytes];
        ReadArchiveBytes(packageHandle, zip64Offset, zip64, "ZIP64 end of central directory");
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != zip64EndOfCentralDirectorySignature
            || BinaryPrimitives.ReadUInt64LittleEndian(zip64[4..]) != 44
            || BinaryPrimitives.ReadUInt16LittleEndian(zip64[14..]) != 45
            || BinaryPrimitives.ReadUInt32LittleEndian(zip64[16..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(zip64[20..]) != 0)
        {
            throw new InvalidDataException("Station package ZIP64 footer is not canonical.");
        }

        ulong entriesOnDisk64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64[24..]);
        ulong entryCount64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64[32..]);
        ulong centralSize64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64[40..]);
        ulong centralOffset64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64[48..]);
        if (entriesOnDisk64 != entryCount64
            || !MatchesCanonicalClassicValue(entryCount16, entryCount64, ushort.MaxValue)
            || !MatchesCanonicalClassicValue(centralSize32, centralSize64, uint.MaxValue)
            || !MatchesCanonicalClassicValue(centralOffset32, centralOffset64, uint.MaxValue)
            || entryCount64 > int.MaxValue
            || centralSize64 > long.MaxValue
            || centralOffset64 > long.MaxValue)
        {
            throw new InvalidDataException(
                "Station package classic and ZIP64 footer values are inconsistent.");
        }

        long centralOffsetFromZip64 = (long)centralOffset64;
        long centralSizeFromZip64 = (long)centralSize64;
        if (centralOffsetFromZip64 > zip64Offset
            || centralSizeFromZip64 != zip64Offset - centralOffsetFromZip64)
        {
            throw new InvalidDataException(
                "Station package central directory and ZIP64 footer are not contiguous.");
        }

        return new ArchiveFooter(
            entryCount64,
            centralOffsetFromZip64,
            centralSizeFromZip64);
    }

    private static bool MatchesCanonicalClassicValue(
        ulong classicValue,
        ulong zip64Value,
        ulong sentinel) =>
        zip64Value >= sentinel
            ? classicValue == sentinel
            : classicValue == zip64Value;

    private static CentralArchiveEntry ReadCentralArchiveEntry(
        SafeFileHandle packageHandle,
        long recordOffset,
        long centralEnd)
    {
        const uint centralDirectoryHeaderSignature = 0x02014b50;
        if (recordOffset > centralEnd - CentralDirectoryHeaderBytes)
        {
            throw new InvalidDataException("Station package central directory is truncated.");
        }

        Span<byte> header = stackalloc byte[CentralDirectoryHeaderBytes];
        ReadArchiveBytes(packageHandle, recordOffset, header, "central directory header");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != centralDirectoryHeaderSignature)
        {
            throw new InvalidDataException("Station package central directory signature is invalid.");
        }

        ushort versionMadeBy = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        ushort versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        ushort compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        ushort modifiedTime = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        ushort modifiedDate = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        uint compressedLength32 = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        uint expandedLength32 = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
        int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
        ushort diskNumberStart = BinaryPrimitives.ReadUInt16LittleEndian(header[34..]);
        ushort internalAttributes = BinaryPrimitives.ReadUInt16LittleEndian(header[36..]);
        uint externalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(header[38..]);
        uint localHeaderOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
        ValidateCanonicalEntryHeader(
            flags,
            compressionMethod,
            commentLength,
            diskNumberStart,
            nameLength,
            extraLength,
            internalAttributes,
            externalAttributes,
            "central directory");

        long variableOffset = checked(recordOffset + CentralDirectoryHeaderBytes);
        long recordLength = checked((long)CentralDirectoryHeaderBytes + nameLength + extraLength);
        if (recordLength > centralEnd - recordOffset)
        {
            throw new InvalidDataException("Station package central directory entry is truncated.");
        }

        byte[] rawName = ReadArchiveBytes(
            packageHandle,
            variableOffset,
            nameLength,
            "central entry name");
        byte[] extra = ReadArchiveBytes(
            packageHandle,
            checked(variableOffset + nameLength),
            extraLength,
            "central entry extra data");
        var fullName = DecodeCanonicalEntryName(rawName, flags);
        var normalized = StationPackagePath.NormalizeRelative(fullName, "Archive entry");
        if (!string.Equals(normalized, fullName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Station package entry '{fullName}' is not a canonical file path.");
        }

        bool zip64Expanded = expandedLength32 == uint.MaxValue;
        bool zip64Compressed = compressedLength32 == uint.MaxValue;
        bool zip64Offset = localHeaderOffset32 == uint.MaxValue;
        Zip64ExtraValues zip64 = ReadCanonicalZip64Extra(
            extra,
            zip64Expanded,
            zip64Compressed,
            zip64Offset,
            fullName);
        ulong expandedLength64 = zip64Expanded
            ? zip64.ExpandedLength
            : expandedLength32;
        ulong compressedLength64 = zip64Compressed
            ? zip64.CompressedLength
            : compressedLength32;
        ulong localHeaderOffset64 = zip64Offset
            ? zip64.LocalHeaderOffset
            : localHeaderOffset32;
        ushort requiredVersion = zip64Expanded || zip64Compressed || zip64Offset
            ? (ushort)45
            : (ushort)20;
        if (compressedLength64 != expandedLength64)
        {
            throw new InvalidDataException(
                $"Station package stored entry '{fullName}' compressed and expanded sizes differ.");
        }

        if (versionMadeBy != requiredVersion
            || versionNeeded != requiredVersion
            || expandedLength64 > long.MaxValue
            || compressedLength64 > long.MaxValue
            || localHeaderOffset64 > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Station package entry '{fullName}' has unsupported ZIP version or ZIP64 values "
                + $"(made-by={versionMadeBy}, version={versionNeeded}, required={requiredVersion}).");
        }

        return new CentralArchiveEntry(
            fullName,
            rawName,
            versionNeeded,
            flags,
            compressionMethod,
            modifiedTime,
            modifiedDate,
            crc32,
            (long)compressedLength64,
            (long)expandedLength64,
            (long)localHeaderOffset64,
            checked(recordOffset + recordLength));
    }

    private static void ValidateCanonicalEntryHeader(
        ushort flags,
        ushort compressionMethod,
        int commentLength,
        ushort diskNumberStart,
        int nameLength,
        int extraLength,
        ushort internalAttributes,
        uint externalAttributes,
        string context)
    {
        if (flags is not 0 and not CanonicalUtf8Flag
            || compressionMethod != CanonicalCompressionMethod
            || commentLength != 0
            || diskNumberStart != 0
            || nameLength is <= 0 or > MaximumCanonicalEntryNameBytes
            || extraLength > MaximumCanonicalCentralExtraBytes
            || internalAttributes != 0
            || externalAttributes != 0)
        {
            throw new InvalidDataException(
                $"Station package {context} entry is not a regular canonical stored file "
                + $"(flags=0x{flags:x4}, method={compressionMethod}, comment={commentLength}, "
                + $"disk={diskNumberStart}, name={nameLength}, extra={extraLength}, "
                + $"internal-attributes=0x{internalAttributes:x4}, "
                + $"external-attributes=0x{externalAttributes:x8}).");
        }
    }

    private static string DecodeCanonicalEntryName(
        ReadOnlySpan<byte> rawName,
        ushort flags)
    {
        try
        {
            if (flags == CanonicalUtf8Flag)
            {
                if (!rawName.ContainsAnyExceptInRange((byte)0, (byte)0x7f))
                {
                    throw new InvalidDataException(
                        "ASCII station package entry names must omit the UTF-8 ZIP flag.");
                }

                return new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(rawName);
            }

            if (rawName.ContainsAnyExceptInRange((byte)0, (byte)0x7f))
            {
                throw new InvalidDataException(
                    "Non-ASCII station package entry names must use the UTF-8 ZIP flag.");
            }

            return Encoding.ASCII.GetString(rawName);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Station package entry name is not valid UTF-8.",
                exception);
        }
    }

    private static Zip64ExtraValues ReadCanonicalZip64Extra(
        ReadOnlySpan<byte> extra,
        bool needsExpandedLength,
        bool needsCompressedLength,
        bool needsLocalHeaderOffset,
        string entryName)
    {
        int valueCount = (needsExpandedLength ? 1 : 0)
                         + (needsCompressedLength ? 1 : 0)
                         + (needsLocalHeaderOffset ? 1 : 0);
        if (valueCount == 0)
        {
            if (!extra.IsEmpty)
            {
                throw new InvalidDataException(
                    $"Station package entry '{entryName}' has non-canonical ZIP extra data.");
            }

            return default;
        }

        int expectedFieldLength = checked(valueCount * sizeof(ulong));
        if (extra.Length != expectedFieldLength + 4
            || BinaryPrimitives.ReadUInt16LittleEndian(extra) != 0x0001
            || BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]) != expectedFieldLength)
        {
            throw new InvalidDataException(
                $"Station package entry '{entryName}' has malformed or non-canonical ZIP64 extra data.");
        }

        ReadOnlySpan<byte> values = extra[4..];
        ulong expandedLength = needsExpandedLength
            ? ReadCanonicalZip64Value(ref values, uint.MaxValue, entryName)
            : 0;
        ulong compressedLength = needsCompressedLength
            ? ReadCanonicalZip64Value(ref values, uint.MaxValue, entryName)
            : 0;
        ulong localHeaderOffset = needsLocalHeaderOffset
            ? ReadCanonicalZip64Value(ref values, uint.MaxValue, entryName)
            : 0;
        if (!values.IsEmpty)
        {
            throw new InvalidDataException(
                $"Station package entry '{entryName}' has trailing ZIP64 values.");
        }

        return new Zip64ExtraValues(
            expandedLength,
            compressedLength,
            localHeaderOffset);
    }

    private static ulong ReadCanonicalZip64Value(
        ref ReadOnlySpan<byte> values,
        ulong minimumValue,
        string entryName)
    {
        if (values.Length < sizeof(ulong))
        {
            throw new InvalidDataException(
                $"Station package entry '{entryName}' has incomplete ZIP64 data.");
        }

        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(values);
        values = values[sizeof(ulong)..];
        if (value < minimumValue)
        {
            throw new InvalidDataException(
                $"Station package entry '{entryName}' uses unnecessary ZIP64 data.");
        }

        return value;
    }

    private static IndexedArchiveEntry ReadLocalArchiveEntry(
        SafeFileHandle packageHandle,
        CentralArchiveEntry central,
        long centralOffset)
    {
        const uint localFileHeaderSignature = 0x04034b50;
        Span<byte> header = stackalloc byte[LocalFileHeaderBytes];
        ReadArchiveBytes(
            packageHandle,
            central.LocalHeaderOffset,
            header,
            $"local header for '{central.FullName}'");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != localFileHeaderSignature)
        {
            throw new InvalidDataException(
                $"Station package entry '{central.FullName}' local header signature is invalid.");
        }

        ushort versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        ushort compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        ushort modifiedTime = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        ushort modifiedDate = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
        uint compressedLength32 = BinaryPrimitives.ReadUInt32LittleEndian(header[18..]);
        uint expandedLength32 = BinaryPrimitives.ReadUInt32LittleEndian(header[22..]);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        if (versionNeeded != central.VersionNeeded
            || flags != central.Flags
            || compressionMethod != central.CompressionMethod
            || modifiedTime != central.ModifiedTime
            || modifiedDate != central.ModifiedDate
            || crc32 != central.Crc32
            || nameLength != central.RawName.Length
            || nameLength is <= 0 or > MaximumCanonicalEntryNameBytes
            || extraLength > MaximumCanonicalCentralExtraBytes)
        {
            throw new InvalidDataException(
                $"Station package entry '{central.FullName}' local and central headers differ.");
        }

        long variableOffset = checked(central.LocalHeaderOffset + LocalFileHeaderBytes);
        byte[] rawName = ReadArchiveBytes(
            packageHandle,
            variableOffset,
            nameLength,
            $"local name for '{central.FullName}'");
        if (!rawName.AsSpan().SequenceEqual(central.RawName))
        {
            throw new InvalidDataException(
                $"Station package entry '{central.FullName}' local and central names differ.");
        }

        byte[] extra = ReadArchiveBytes(
            packageHandle,
            checked(variableOffset + nameLength),
            extraLength,
            $"local extra data for '{central.FullName}'");
        bool zip64Expanded = expandedLength32 == uint.MaxValue;
        bool zip64Compressed = compressedLength32 == uint.MaxValue;
        Zip64ExtraValues zip64 = ReadCanonicalZip64Extra(
            extra,
            zip64Expanded,
            zip64Compressed,
            needsLocalHeaderOffset: false,
            central.FullName);
        ulong expandedLength64 = zip64Expanded
            ? zip64.ExpandedLength
            : expandedLength32;
        ulong compressedLength64 = zip64Compressed
            ? zip64.CompressedLength
            : compressedLength32;
        if (expandedLength64 != (ulong)central.ExpandedLength
            || compressedLength64 != (ulong)central.CompressedLength)
        {
            throw new InvalidDataException(
                $"Station package entry '{central.FullName}' local and central sizes differ.");
        }

        long dataOffset = checked(variableOffset + nameLength + extraLength);
        if (dataOffset > centralOffset
            || central.CompressedLength > centralOffset - dataOffset)
        {
            throw new InvalidDataException(
                $"Station package entry '{central.FullName}' compressed data is truncated.");
        }

        return new IndexedArchiveEntry(
            packageHandle,
            central.FullName,
            dataOffset,
            central.CompressedLength,
            central.ExpandedLength,
            central.Crc32,
            central.ModifiedTime,
            central.ModifiedDate,
            checked(dataOffset + central.CompressedLength));
    }

    private static void ValidateCanonicalArchiveOrder(
        IReadOnlyList<CentralArchiveEntry> entries)
    {
        if (entries.Count < 3
            || !string.Equals(entries[^2].FullName, ManifestEntryName, StringComparison.Ordinal)
            || !string.Equals(entries[^1].FullName, SignatureEntryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Station package metadata entries must be the final canonical entries.");
        }

        string? priorPath = null;
        for (var index = 0; index < entries.Count - 2; index++)
        {
            var path = entries[index].FullName;
            if (path is ManifestEntryName or SignatureEntryName
                || (priorPath is not null
                    && StringComparer.Ordinal.Compare(priorPath, path) >= 0))
            {
                throw new InvalidDataException(
                    "Station package content entries are not in canonical ordinal order.");
            }

            priorPath = path;
        }
    }

    private static byte[] ReadArchiveBytes(
        SafeFileHandle packageHandle,
        long offset,
        int length,
        string context)
    {
        var bytes = new byte[length];
        ReadArchiveBytes(packageHandle, offset, bytes, context);
        return bytes;
    }

    private static void ReadArchiveBytes(
        SafeFileHandle packageHandle,
        long offset,
        Span<byte> destination,
        string context)
    {
        if (offset < 0)
        {
            throw new InvalidDataException(
                $"Station package ZIP has an invalid offset while reading {context}.");
        }

        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            int bytesRead = RandomAccess.Read(
                packageHandle,
                destination[totalRead..],
                checked(offset + totalRead));
            if (bytesRead == 0)
            {
                throw new InvalidDataException(
                    $"Station package ZIP is truncated while reading {context}.");
            }

            totalRead += bytesRead;
        }
    }

    private void ValidateManifest(
        StationPackageManifest manifest,
        string expectedHash,
        long expandedMetadataBytes)
    {
        if (!string.Equals(manifest.Format, StationPackageManifest.RequiredFormat, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported station package format '{manifest.Format}'.");
        }

        _ = Required(manifest.PackageId, nameof(manifest.PackageId));
        _ = Required(manifest.ProjectId, nameof(manifest.ProjectId));
        _ = Required(manifest.ApplicationId, nameof(manifest.ApplicationId));
        _ = Required(manifest.ProjectSnapshotId, nameof(manifest.ProjectSnapshotId));
        _ = Required(
            manifest.ProductionLineDefinitionId,
            nameof(manifest.ProductionLineDefinitionId));
        _ = Required(manifest.StationSystemId, nameof(manifest.StationSystemId));
        if (manifest.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Station package creation timestamp must use UTC offset zero.");
        }

        if (manifest.Entries.Count == 0 || manifest.Entries.Count > _maximumEntryCount)
        {
            throw new InvalidDataException("Station package entry count is outside the allowed range.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var windowsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        string? priorPath = null;
        foreach (StationPackageEntry entry in manifest.Entries)
        {
            var path = StationPackagePath.NormalizeRelative(entry.Path, nameof(entry.Path));
            if (!string.Equals(path, entry.Path, StringComparison.Ordinal)
                || path is ManifestEntryName or SignatureEntryName
                || (priorPath is not null && StringComparer.Ordinal.Compare(priorPath, path) >= 0)
                || !paths.Add(path)
                || !windowsPaths.Add(path))
            {
                throw new InvalidDataException(
                    $"Station package manifest path '{entry.Path}' is duplicated or not strictly ordered.");
            }

            if (entry.Length < 0)
            {
                throw new InvalidDataException($"Station package entry '{path}' has a negative length.");
            }

            totalBytes = checked(totalBytes + entry.Length);
            _ = RequireSha256(entry.Sha256, nameof(entry.Sha256));
            _ = Required(entry.MediaType, nameof(entry.MediaType));
            priorPath = path;
        }

        if (expandedMetadataBytes > _maximumExpandedBytes - totalBytes)
        {
            throw new InvalidDataException("Station package expanded size exceeds the configured limit.");
        }

        var manifestHash = RequireSha256(manifest.ContentSha256, nameof(manifest.ContentSha256));
        if (!string.Equals(
                StationPackageCanonicalization.ComputeContentSha256(
                    manifest.ProjectId,
                    manifest.ApplicationId,
                    manifest.ProjectSnapshotId,
                    manifest.ProductionLineDefinitionId,
                    manifest.StationSystemId,
                    manifest.Entries),
                manifestHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Station package content hash does not match its entry manifest.");
        }

        if (!string.Equals(manifestHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Station package content hash '{manifestHash}' does not match requested '{expectedHash}'.");
        }
    }

    private void VerifySignature(ReadOnlySpan<byte> manifestBytes, StationPackageSignature signature)
    {
        if (!string.Equals(
                signature.Algorithm,
                StationPackageSignature.RequiredAlgorithm,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported station package signature algorithm '{signature.Algorithm}'.");
        }

        var keyId = Required(signature.KeyId, nameof(signature.KeyId));
        if (!_trustedPublicKeys.TryGetValue(keyId, out var publicKeyPem))
        {
            throw new InvalidDataException(
                $"Station package signing key '{keyId}' is not trusted by this Agent.");
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(Required(signature.Signature, nameof(signature.Signature)));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Station package signature is not valid Base64.", exception);
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        if (rsa.KeySize < 3072)
        {
            throw new InvalidDataException(
                "Trusted Station package RSA public key must be at least 3072 bits.");
        }

        if (!rsa.VerifyData(
                manifestBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss))
        {
            throw new InvalidDataException("Station package signature verification failed.");
        }
    }

    private static void ValidateArchiveIndex(
        IReadOnlyDictionary<string, IndexedArchiveEntry> archiveEntries,
        StationPackageManifest manifest)
    {
        var expected = manifest.Entries
            .Select(entry => entry.Path)
            .Append(ManifestEntryName)
            .Append(SignatureEntryName)
            .ToHashSet(StringComparer.Ordinal);
        var unexpected = archiveEntries.Keys.Where(path => !expected.Contains(path)).ToArray();
        var missing = expected.Where(path => !archiveEntries.ContainsKey(path)).ToArray();
        if (unexpected.Length != 0 || missing.Length != 0)
        {
            throw new InvalidDataException(
                $"Station package archive differs from its manifest. "
                + $"Unexpected=[{string.Join(',', unexpected)}], Missing=[{string.Join(',', missing)}].");
        }

        uint dosTimestamp = StationPackageCanonicalization.CanonicalDosTimestamp(
            manifest.CreatedAtUtc);
        ushort expectedTime = (ushort)(dosTimestamp & ushort.MaxValue);
        ushort expectedDate = (ushort)(dosTimestamp >> 16);
        if (archiveEntries.Values.Any(entry =>
                entry.ModifiedTime != expectedTime
                || entry.ModifiedDate != expectedDate))
        {
            throw new InvalidDataException(
                "Station package entry timestamps do not match the signed creation timestamp.");
        }
    }

    private static async ValueTask VerifyFileAsync(
        string path,
        StationPackageEntry entry,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != entry.Length)
        {
            throw new InvalidDataException(
                $"Station package file '{entry.Path}' length verification failed.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actualHash, entry.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Station package file '{entry.Path}' SHA-256 verification failed.");
        }
    }

    private static async ValueTask<byte[]> ReadMetadataAsync(
        IndexedArchiveEntry entry,
        int maximumMetadataBytes,
        long remainingExpandedBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length <= 0 || entry.Length > maximumMetadataBytes)
        {
            throw new InvalidDataException(
                $"Station package metadata '{entry.FullName}' has an invalid length.");
        }

        using var output = new MemoryStream(checked((int)entry.Length));
        _ = await CopyExpandedEntryAsync(
                entry,
                output,
                entry.Length,
                Math.Min(maximumMetadataBytes, remainingExpandedBytes),
                entry.FullName,
                cancellationToken)
            .ConfigureAwait(false);
        return output.ToArray();
    }

    private static async ValueTask<long> CopyExpandedEntryAsync(
        IndexedArchiveEntry entry,
        Stream output,
        long declaredLength,
        long maximumBytes,
        string entryName,
        CancellationToken cancellationToken)
    {
        if (declaredLength < 0 || declaredLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"Station package entry '{entryName}' exceeds the expanded-content limit.");
        }

        if (declaredLength != entry.ExpandedLength)
        {
            throw new InvalidDataException(
                $"Station package entry '{entryName}' length does not match its canonical header.");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ExpandedContentCopyBufferBytes);
        try
        {
            if (entry.CompressedLength != declaredLength)
            {
                throw new InvalidDataException(
                    $"Station package stored entry '{entryName}' compressed and expanded sizes differ.");
            }

            long copiedBytes = 0;
            var crc32 = new Crc32Accumulator();
            while (copiedBytes < declaredLength)
            {
                int readLength = (int)Math.Min(buffer.Length, declaredLength - copiedBytes);
                int bytesRead = await RandomAccess.ReadAsync(
                        entry.PackageHandle,
                        buffer.AsMemory(0, readLength),
                        checked(entry.DataOffset + copiedBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    throw new InvalidDataException(
                        $"Station package entry '{entryName}' stored data is truncated.");
                }

                crc32.Append(buffer.AsSpan(0, bytesRead));
                await output.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        cancellationToken)
                    .ConfigureAwait(false);
                copiedBytes = checked(copiedBytes + bytesRead);
            }

            if (crc32.Value != entry.Crc32)
            {
                throw new InvalidDataException(
                    $"Station package entry '{entryName}' CRC-32 verification failed.");
            }

            return copiedBytes;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static T DeserializeCanonical<T>(byte[] bytes, string entryName)
    {
        T value;
        try
        {
            value = JsonSerializer.Deserialize<T>(bytes, StationPackageJson.Options)
                ?? throw new InvalidDataException($"Station package metadata '{entryName}' is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Station package metadata '{entryName}' is invalid JSON.",
                exception);
        }

        var canonical = JsonSerializer.SerializeToUtf8Bytes(value, StationPackageJson.Options);
        if (!bytes.AsSpan().SequenceEqual(canonical))
        {
            throw new InvalidDataException(
                $"Station package metadata '{entryName}' is not canonical JSON.");
        }

        return value;
    }

    private sealed record ArchiveFooter(
        ulong EntryCount,
        long CentralOffset,
        long CentralSize);

    private sealed record CentralArchiveEntry(
        string FullName,
        byte[] RawName,
        ushort VersionNeeded,
        ushort Flags,
        ushort CompressionMethod,
        ushort ModifiedTime,
        ushort ModifiedDate,
        uint Crc32,
        long CompressedLength,
        long ExpandedLength,
        long LocalHeaderOffset,
        long RecordEndOffset);

    private readonly record struct Zip64ExtraValues(
        ulong ExpandedLength,
        ulong CompressedLength,
        ulong LocalHeaderOffset);

    private sealed record IndexedArchiveEntry(
        SafeFileHandle PackageHandle,
        string FullName,
        long DataOffset,
        long CompressedLength,
        long ExpandedLength,
        uint Crc32,
        ushort ModifiedTime,
        ushort ModifiedDate,
        long EndOffset)
    {
        public long Length => ExpandedLength;
    }

    private sealed class Crc32Accumulator
    {
        private static readonly uint[] Table = CreateTable();
        private uint _state = uint.MaxValue;

        public uint Value => ~_state;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            uint state = _state;
            foreach (byte value in bytes)
            {
                state = Table[(state ^ value) & 0xff] ^ (state >> 8);
            }

            _state = state;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? 0xedb88320U ^ (value >> 1)
                        : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }

    private static IndexedArchiveEntry RequiredEntry(
        Dictionary<string, IndexedArchiveEntry> entries,
        string path) =>
        entries.TryGetValue(path, out IndexedArchiveEntry? entry)
            ? entry
            : throw new InvalidDataException($"Station package is missing '{path}'.");

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException($"{parameterName} must be canonical non-empty text.")
            : value;

    private static string RequireSha256(string value, string parameterName) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new InvalidDataException($"{parameterName} must be a lowercase SHA-256.");

    private static string ResolveImmutableReaderSid(string? configuredSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return configuredSid ?? "unix-reader";
        }

        if (!string.IsNullOrWhiteSpace(configuredSid))
        {
            return configuredSid;
        }

        return WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
    }

    private static string? ResolveImmutableStationServiceSid(string? configuredSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return configuredSid;
        }

        return WindowsStationServiceIdentityReader.RequireCanonicalServiceSid(
            configuredSid,
            nameof(StationPackageTrustOptions.ImmutableStationServiceSid));
    }

}
