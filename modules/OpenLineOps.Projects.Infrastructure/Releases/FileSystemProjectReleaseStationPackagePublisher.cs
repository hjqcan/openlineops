using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Projects.Application.Releases;

namespace OpenLineOps.Projects.Infrastructure.Releases;

public sealed record StationPackagePublicationOptions(
    string DistributionDirectory,
    string DeploymentCatalogDirectory,
    string SigningKeyId,
    string SigningPrivateKeyPath)
{
    public const string SectionName = "OpenLineOps:Projects:StationPackages";
}

public sealed class FileSystemProjectReleaseStationPackagePublisher(
    StationPackagePublicationOptions options) : IProjectReleaseStationPackagePublisher
{
    private static readonly JsonSerializerOptions JsonOptions =
        StationPackageCanonicalization.CreateJsonOptions();

    public async ValueTask ValidateConfigurationAsync(
        ProjectReleaseStationPackagePreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        cancellationToken.ThrowIfCancellationRequested();
        var distributionDirectory = ConfiguredDirectory(
            options.DistributionDirectory,
            nameof(options.DistributionDirectory));
        var catalogDirectory = ConfiguredDirectory(
            options.DeploymentCatalogDirectory,
            nameof(options.DeploymentCatalogDirectory));
        _ = Configured(options.SigningKeyId, nameof(options.SigningKeyId));
        var privateKeyPath = ConfiguredFile(
            options.SigningPrivateKeyPath,
            nameof(options.SigningPrivateKeyPath));
        var releaseRootPath = ProjectReleaseArtifactPath.GetReleaseRootPath(
            request.Scope,
            Configured(request.ProjectSnapshotId, nameof(request.ProjectSnapshotId)));
        EnsureOutsideRelease(distributionDirectory, releaseRootPath);
        EnsureOutsideRelease(catalogDirectory, releaseRootPath);
        EnsureOutsideRoot(privateKeyPath, request.Scope.ProjectPath, "Automation project");
        EnsureOutsideRoot(privateKeyPath, distributionDirectory, "Station package distribution");
        EnsureOutsideRoot(privateKeyPath, catalogDirectory, "Station deployment catalog");
        await EnsurePrivateKeyIsNotCopiedIntoProjectAsync(
                privateKeyPath,
                request.Scope.ProjectPath,
                cancellationToken)
            .ConfigureAwait(false);

        var privateKeyPem = await File.ReadAllTextAsync(
                privateKeyPath,
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(privateKeyPem);
            if (rsa.KeySize < 3072)
            {
                throw new InvalidDataException(
                    "Station package signing RSA key must be at least 3072 bits.");
            }

            _ = rsa.SignData(
                "openlineops-station-package-preflight"u8,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        }

        VerifyWritableDirectory(distributionDirectory);
        VerifyWritableDirectory(catalogDirectory);
    }

    public async ValueTask<ProjectReleaseStationPackageSet> PublishAsync(
        ProjectReleaseStationPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var release = request.Release;
        var distributionDirectory = ConfiguredDirectory(
            options.DistributionDirectory,
            nameof(options.DistributionDirectory));
        var catalogDirectory = ConfiguredDirectory(
            options.DeploymentCatalogDirectory,
            nameof(options.DeploymentCatalogDirectory));
        var signingKeyId = Configured(options.SigningKeyId, nameof(options.SigningKeyId));
        var privateKeyPath = ConfiguredFile(
            options.SigningPrivateKeyPath,
            nameof(options.SigningPrivateKeyPath));
        EnsureOutsideRelease(distributionDirectory, release.ReleaseRootPath);
        EnsureOutsideRelease(catalogDirectory, release.ReleaseRootPath);
        EnsureOutsideRoot(privateKeyPath, release.ReleaseRootPath, "immutable release");
        EnsureOutsideRoot(privateKeyPath, distributionDirectory, "Station package distribution");
        EnsureOutsideRoot(privateKeyPath, catalogDirectory, "Station deployment catalog");
        if (request.PublishedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Station package publication timestamp must use UTC offset zero.");
        }

        var stationSystemIds = ProjectReleaseStationDeploymentSet
            .Resolve(request.Metadata.ProductionLine)
            .Select(stationSystemId => Configured(stationSystemId, nameof(stationSystemId)))
            .ToArray();
        if (stationSystemIds.Length == 0)
        {
            throw new InvalidDataException("A release must contain at least one Station operation.");
        }

        Directory.CreateDirectory(distributionDirectory);
        Directory.CreateDirectory(catalogDirectory);
        var privateKeyPem = await File.ReadAllTextAsync(
                privateKeyPath,
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        var published = new List<ProjectReleaseStationPackage>(stationSystemIds.Length);
        var createdPaths = new List<string>(stationSystemIds.Length * 2);
        try
        {
            foreach (var stationSystemId in stationSystemIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temporaryPackagePath = Path.Combine(
                    distributionDirectory,
                    $".{Guid.NewGuid():N}.olopkg");
                var built = await SignedStationPackageBuilder.BuildAsync(
                        new BuildStationPackageRequest(
                            release.ReleaseRootPath,
                            temporaryPackagePath,
                            $"{release.ProjectId}/{release.ApplicationId}/{release.SnapshotId}/{stationSystemId}",
                            release.ProjectId,
                            release.ApplicationId,
                            release.SnapshotId,
                            stationSystemId,
                            signingKeyId,
                            privateKeyPem,
                            request.PublishedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
                createdPaths.Add(temporaryPackagePath);
                var packagePath = Path.Combine(
                    distributionDirectory,
                    $"{built.Manifest.ContentSha256}.olopkg");
                File.Move(temporaryPackagePath, packagePath, overwrite: false);
                createdPaths.Remove(temporaryPackagePath);
                createdPaths.Add(packagePath);

                var deployment = new StationPackageDeployment(
                    StationPackageDeployment.RequiredSchema,
                    release.ProjectId,
                    release.ApplicationId,
                    release.SnapshotId,
                    stationSystemId,
                    built.Manifest.ContentSha256,
                    request.PublishedAtUtc);
                var catalogPath = StationPackageCanonicalization.DeploymentCatalogPath(
                    catalogDirectory,
                    release.ProjectId,
                    release.ApplicationId,
                    release.SnapshotId,
                    stationSystemId);
                await WriteNewCanonicalJsonAsync(catalogPath, deployment, cancellationToken)
                    .ConfigureAwait(false);
                createdPaths.Add(catalogPath);
                published.Add(new ProjectReleaseStationPackage(
                    stationSystemId,
                    built.Manifest.ContentSha256,
                    packagePath,
                    catalogPath));
            }

            return new ProjectReleaseStationPackageSet(
                release.ProjectId,
                release.ApplicationId,
                release.SnapshotId,
                published);
        }
        catch
        {
            foreach (var path in createdPaths)
            {
                File.Delete(path);
            }

            throw;
        }
    }

    public ValueTask RollbackAsync(
        ProjectReleaseStationPackageSet packages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);
        cancellationToken.ThrowIfCancellationRequested();
        var distributionDirectory = ConfiguredDirectory(
            options.DistributionDirectory,
            nameof(options.DistributionDirectory));
        var catalogDirectory = ConfiguredDirectory(
            options.DeploymentCatalogDirectory,
            nameof(options.DeploymentCatalogDirectory));
        foreach (var package in packages.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedPackagePath = Path.Combine(
                distributionDirectory,
                $"{RequireSha256(package.PackageContentSha256)}.olopkg");
            var expectedCatalogPath = StationPackageCanonicalization.DeploymentCatalogPath(
                catalogDirectory,
                packages.ProjectId,
                packages.ApplicationId,
                packages.ProjectSnapshotId,
                package.StationSystemId);
            if (!string.Equals(
                    Path.GetFullPath(package.PackagePath),
                    expectedPackagePath,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFullPath(package.DeploymentCatalogPath),
                    expectedCatalogPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Station package rollback descriptor escapes its configured publication roots.");
            }

            File.Delete(expectedCatalogPath);
            File.Delete(expectedPackagePath);
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask WriteNewCanonicalJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string ConfiguredDirectory(string value, string name)
    {
        var configured = Configured(value, name);
        return Path.GetFullPath(configured, AppContext.BaseDirectory);
    }

    private static string RequireSha256(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new InvalidDataException("Station package content SHA-256 is invalid.");

    private static void VerifyWritableDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var probePath = Path.Combine(path, $".{Guid.NewGuid():N}.preflight");
        try
        {
            using var probe = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose | FileOptions.WriteThrough);
            probe.WriteByte(0);
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    private static string ConfiguredFile(string value, string name)
    {
        var path = Path.GetFullPath(Configured(value, name), AppContext.BaseDirectory);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Configured {name} does not exist.", path);
    }

    private static string Configured(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException(
                $"{StationPackagePublicationOptions.SectionName}:{name} is required and must be canonical text.")
            : value;

    private static void EnsureOutsideRelease(string path, string releaseRootPath)
    {
        var releaseRoot = Path.GetFullPath(releaseRootPath)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (path.StartsWith(releaseRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Station package distribution and catalog paths must be outside the immutable release.");
        }
    }

    private static void EnsureOutsideRoot(string candidatePath, string rootPath, string rootName)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = root + Path.DirectorySeparatorChar;
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Station package signing private key must be outside the {rootName} root '{root}'.");
        }
    }

    private static async ValueTask EnsurePrivateKeyIsNotCopiedIntoProjectAsync(
        string privateKeyPath,
        string projectPath,
        CancellationToken cancellationToken)
    {
        var privateKeyInfo = new FileInfo(privateKeyPath);
        await using var privateKeyStream = new FileStream(
            privateKeyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var privateKeySha256 = await SHA256.HashDataAsync(privateKeyStream, cancellationToken)
            .ConfigureAwait(false);
        foreach (var candidatePath in Directory.EnumerateFiles(
                     projectPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(candidatePath).Length != privateKeyInfo.Length)
            {
                continue;
            }

            await using var candidateStream = new FileStream(
                candidatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var candidateSha256 = await SHA256.HashDataAsync(candidateStream, cancellationToken)
                .ConfigureAwait(false);
            if (candidateSha256.AsSpan().SequenceEqual(privateKeySha256))
            {
                throw new InvalidDataException(
                    $"Station package signing private key material is copied inside the Automation project at '{candidatePath}'.");
            }
        }
    }
}
