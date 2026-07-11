using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Packages;

public sealed record StationPackageTrustOptions(
    string ContentCacheDirectory,
    IReadOnlyDictionary<string, string> TrustedPublicKeys,
    long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024,
    int MaximumEntryCount = 20_000);

public sealed record InstalledStationPackage(
    string ContentDirectory,
    StationPackageManifest Manifest);

public sealed class SignedStationPackageInstaller
{
    private const string ManifestEntryName = "package.manifest.json";
    private const string SignatureEntryName = "package.signature.json";
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private const int MaximumSignatureBytes = 64 * 1024;

    private readonly string _cacheDirectory;
    private readonly Dictionary<string, string> _trustedPublicKeys;
    private readonly long _maximumExpandedBytes;
    private readonly int _maximumEntryCount;

    public SignedStationPackageInstaller(StationPackageTrustOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContentCacheDirectory);
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
        _maximumExpandedBytes = options.MaximumExpandedBytes;
        _maximumEntryCount = options.MaximumEntryCount;
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async ValueTask<InstalledStationPackage> InstallAsync(
        string packagePath,
        string expectedContentSha256,
        CancellationToken cancellationToken = default)
    {
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
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
        var archiveEntries = IndexEntries(archive);
        var manifestBytes = await ReadMetadataAsync(
                RequiredEntry(archiveEntries, ManifestEntryName),
                MaximumManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var signatureBytes = await ReadMetadataAsync(
                RequiredEntry(archiveEntries, SignatureEntryName),
                MaximumSignatureBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var manifest = DeserializeCanonical<StationPackageManifest>(manifestBytes, ManifestEntryName);
        var signature = DeserializeCanonical<StationPackageSignature>(signatureBytes, SignatureEntryName);
        ValidateManifest(manifest, expectedHash);
        VerifySignature(manifestBytes, signature);
        ValidateArchiveIndex(archiveEntries, manifest);

        var contentDirectory = Path.Combine(_cacheDirectory, manifest.ContentSha256);
        if (Directory.Exists(contentDirectory))
        {
            await VerifyInstalledContentAsync(contentDirectory, manifest, cancellationToken)
                .ConfigureAwait(false);
            return new InstalledStationPackage(contentDirectory, manifest);
        }

        var stagingDirectory = Path.Combine(
            _cacheDirectory,
            $".{manifest.ContentSha256}.{Guid.NewGuid():N}.installing");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            foreach (var declared in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var zipEntry = archiveEntries[declared.Path];
                if (zipEntry.Length != declared.Length)
                {
                    throw new InvalidDataException(
                        $"Station package entry '{declared.Path}' length does not match its manifest.");
                }

                var outputPath = StationPackagePath.ResolveInside(stagingDirectory, declared.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using (var input = zipEntry.Open())
                await using (var output = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                await VerifyFileAsync(outputPath, declared, cancellationToken).ConfigureAwait(false);
            }

            MakeReadOnly(stagingDirectory);
            try
            {
                Directory.Move(stagingDirectory, contentDirectory);
            }
            catch (IOException) when (Directory.Exists(contentDirectory))
            {
                ClearReadOnlyAndDelete(stagingDirectory);
                await VerifyInstalledContentAsync(contentDirectory, manifest, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new InstalledStationPackage(contentDirectory, manifest);
        }
        catch
        {
            ClearReadOnlyAndDelete(stagingDirectory);
            throw;
        }
    }

    private Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > checked(_maximumEntryCount + 2))
        {
            throw new InvalidDataException("Station package contains too many entries.");
        }

        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var caseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = StationPackagePath.NormalizeRelative(entry.FullName, "Archive entry");
            if (!string.Equals(path, entry.FullName, StringComparison.Ordinal)
                || entry.Name.Length == 0
                || IsSymbolicLink(entry))
            {
                throw new InvalidDataException(
                    $"Station package entry '{entry.FullName}' is not a regular canonical file.");
            }

            if (!result.TryAdd(path, entry) || !caseInsensitive.Add(path))
            {
                throw new InvalidDataException(
                    $"Station package contains duplicate path '{entry.FullName}'.");
            }
        }

        return result;
    }

    private void ValidateManifest(StationPackageManifest manifest, string expectedHash)
    {
        if (!string.Equals(manifest.Format, StationPackageManifest.RequiredFormat, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported station package format '{manifest.Format}'.");
        }

        _ = Required(manifest.PackageId, nameof(manifest.PackageId));
        _ = Required(manifest.ProjectId, nameof(manifest.ProjectId));
        _ = Required(manifest.ApplicationId, nameof(manifest.ApplicationId));
        _ = Required(manifest.ProjectSnapshotId, nameof(manifest.ProjectSnapshotId));
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
        foreach (var entry in manifest.Entries)
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

        if (totalBytes > _maximumExpandedBytes)
        {
            throw new InvalidDataException("Station package expanded size exceeds the configured limit.");
        }

        var manifestHash = RequireSha256(manifest.ContentSha256, nameof(manifest.ContentSha256));
        if (!string.Equals(StationPackageContentHasher.Compute(manifest.Entries), manifestHash, StringComparison.Ordinal))
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
        if (!rsa.VerifyData(
                manifestBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))
        {
            throw new InvalidDataException("Station package signature verification failed.");
        }
    }

    private static void ValidateArchiveIndex(
        IReadOnlyDictionary<string, ZipArchiveEntry> archiveEntries,
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
    }

    private static async ValueTask VerifyInstalledContentAsync(
        string contentDirectory,
        StationPackageManifest manifest,
        CancellationToken cancellationToken)
    {
        var expectedPaths = manifest.Entries
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualPaths = Directory
            .EnumerateFiles(contentDirectory, "*", SearchOption.AllDirectories)
            .Select(path => StationPackagePath.FromFile(contentDirectory, path))
            .ToArray();
        if (actualPaths.Length != manifest.Entries.Count
            || actualPaths.Any(path => !expectedPaths.Contains(path)))
        {
            throw new InvalidDataException(
                $"Installed station package cache '{contentDirectory}' contains unexpected content.");
        }

        foreach (var entry in manifest.Entries)
        {
            await VerifyFileAsync(
                    StationPackagePath.ResolveInside(contentDirectory, entry.Path),
                    entry,
                    cancellationToken)
                .ConfigureAwait(false);
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
        ZipArchiveEntry entry,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length <= 0 || entry.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"Station package metadata '{entry.FullName}' has an invalid length.");
        }

        await using var input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
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

    private static ZipArchiveEntry RequiredEntry(
        Dictionary<string, ZipArchiveEntry> entries,
        string path) =>
        entries.TryGetValue(path, out var entry)
            ? entry
            : throw new InvalidDataException($"Station package is missing '{path}'.");

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

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

    private static void MakeReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(child, File.GetAttributes(child) | FileAttributes.ReadOnly);
        }

        File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly);
    }

    private static void ClearReadOnlyAndDelete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     directory,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        Directory.Delete(directory, recursive: true);
    }
}
