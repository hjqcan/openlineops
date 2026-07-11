using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Packages;

public sealed record BuildStationPackageRequest(
    string SourceDirectory,
    string PackagePath,
    string PackageId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string SigningKeyId,
    string SigningPrivateKeyPem,
    DateTimeOffset CreatedAtUtc);

public sealed record BuiltStationPackage(
    string PackagePath,
    StationPackageManifest Manifest);

public sealed class SignedStationPackageBuilder
{
    private const string ManifestEntryName = "package.manifest.json";
    private const string SignatureEntryName = "package.signature.json";

    public static async ValueTask<BuiltStationPackage> BuildAsync(
        BuildStationPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceDirectory = ExistingDirectory(request.SourceDirectory);
        var packagePath = PackagePath(request.PackagePath, sourceDirectory);
        var createdAtUtc = RequireUtc(request.CreatedAtUtc, nameof(request.CreatedAtUtc));
        var sourceFiles = Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new SourceFile(
                path,
                StationPackagePath.FromFile(sourceDirectory, path)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            throw new InvalidDataException("A station package cannot be built from an empty directory.");
        }

        if (sourceFiles.Any(file => file.RelativePath is ManifestEntryName or SignatureEntryName))
        {
            throw new InvalidDataException(
                "Station package metadata paths are reserved and cannot appear in source content.");
        }

        var entries = new List<StationPackageEntry>(sourceFiles.Length);
        foreach (var source in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(source.AbsolutePath);
            await using var stream = new FileStream(
                source.AbsolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sha256 = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            entries.Add(new StationPackageEntry(
                source.RelativePath,
                fileInfo.Length,
                sha256,
                MediaType(source.RelativePath)));
        }

        var manifest = new StationPackageManifest(
            StationPackageManifest.RequiredFormat,
            Required(request.PackageId, nameof(request.PackageId)),
            Required(request.ProjectId, nameof(request.ProjectId)),
            Required(request.ApplicationId, nameof(request.ApplicationId)),
            Required(request.ProjectSnapshotId, nameof(request.ProjectSnapshotId)),
            StationPackageContentHasher.Compute(entries),
            createdAtUtc,
            entries);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            StationPackageJson.Options);
        var signature = Sign(
            manifestBytes,
            Required(request.SigningKeyId, nameof(request.SigningKeyId)),
            Required(request.SigningPrivateKeyPem, nameof(request.SigningPrivateKeyPem)));
        var signatureBytes = JsonSerializer.SerializeToUtf8Bytes(
            signature,
            StationPackageJson.Options);

        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        var temporaryPath = packagePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var source in sourceFiles)
                {
                    var zipEntry = archive.CreateEntry(source.RelativePath, CompressionLevel.SmallestSize);
                    zipEntry.LastWriteTime = createdAtUtc;
                    await using var target = zipEntry.Open();
                    await using var input = new FileStream(
                        source.AbsolutePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
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

            File.Move(temporaryPath, packagePath, overwrite: true);
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
        var signature = rsa.SignData(
            manifestBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
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
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        entry.LastWriteTime = createdAtUtc;
        await using var stream = entry.Open();
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private static string ExistingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return Directory.Exists(fullPath)
            ? fullPath
            : throw new DirectoryNotFoundException($"Station package source '{fullPath}' does not exist.");
    }

    private static string PackagePath(string path, string sourceDirectory)
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

    private sealed record SourceFile(string AbsolutePath, string RelativePath);
}
