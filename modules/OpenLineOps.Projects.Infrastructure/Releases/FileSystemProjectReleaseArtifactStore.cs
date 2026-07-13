using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Projects.Infrastructure.Releases;

public sealed class FileSystemProjectReleaseArtifactStore :
    IProjectReleaseArtifactStore,
    IInstalledProjectReleaseReader
{
    private const int FileBufferSize = 64 * 1024;
    private static readonly string[] SupportedRouteJudgements =
        ["Passed", "Failed", "Aborted", "Unknown", "NotApplicable"];
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async ValueTask<ProjectReleaseArtifactDescriptor> PublishAsync(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        DateTimeOffset publishedAtUtc,
        ProjectReleaseSourceMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSnapshotId = RequireValue(snapshotId, nameof(snapshotId));
        var normalizedMetadata = NormalizeMetadata(metadata);
        var sourceApplicationPath = ProjectReleaseArtifactPath.GetSourceApplicationPath(scope);
        if (!Directory.Exists(sourceApplicationPath))
        {
            throw new DirectoryNotFoundException(
                $"Project application source directory '{sourceApplicationPath}' does not exist.");
        }

        var releasesPath = ProjectReleaseArtifactPath.GetReleasesPath(scope);
        var releaseRootPath = ProjectReleaseArtifactPath.GetReleaseRootPath(scope, normalizedSnapshotId);
        if (Directory.Exists(releaseRootPath) || File.Exists(releaseRootPath))
        {
            throw new IOException(
                $"Project release {normalizedSnapshotId} already exists at '{releaseRootPath}' and is immutable.");
        }

        Directory.CreateDirectory(releasesPath);
        var stagingRootPath = ProjectReleaseArtifactPath.GetStagingRootPath(scope, normalizedSnapshotId);
        ProjectReleaseArtifactPath.EnsureStagingPath(releasesPath, stagingRootPath);
        Directory.CreateDirectory(stagingRootPath);

        try
        {
            var sourceRootPath = ProjectReleaseArtifactPath.GetSourceRootPath(stagingRootPath);
            var sourceApplicationRelativePath = ProjectReleaseArtifactPath
                .GetSourceApplicationRelativePath(scope);
            var stagedApplicationPath = ProjectReleaseArtifactPath.ResolveRelativePath(
                sourceRootPath,
                sourceApplicationRelativePath);
            var sourceTree = InspectFileTree(sourceApplicationPath);
            var releaseSourceTree = FilterReleaseSourceTree(sourceTree, normalizedMetadata);

            Directory.CreateDirectory(stagedApplicationPath);
            var files = await CopySourceAsync(
                    sourceApplicationPath,
                    stagedApplicationPath,
                    sourceRootPath,
                    releaseSourceTree,
                    cancellationToken)
                .ConfigureAwait(false);
            await VerifySourceUnchangedAsync(
                    sourceApplicationPath,
                    sourceTree,
                    releaseSourceTree,
                    files,
                    sourceApplicationRelativePath,
                    cancellationToken)
                .ConfigureAwait(false);
            await CopyPackagesAsync(
                    stagingRootPath,
                    normalizedMetadata.PackageDependencies,
                    cancellationToken)
                .ConfigureAwait(false);

            var manifest = new ProjectReleaseManifest(
                ProjectReleaseManifest.RequiredSchema,
                ProjectReleaseManifest.RequiredSchemaVersion,
                normalizedSnapshotId,
                scope.ProjectId,
                scope.ApplicationId,
                publishedAtUtc.ToUniversalTime(),
                sourceApplicationRelativePath,
                scope.ApplicationProjectRelativePath,
                normalizedMetadata,
                files,
                string.Empty);
            manifest = manifest with { ContentSha256 = ComputeContentSha256(manifest) };

            await WriteManifestAsync(stagingRootPath, manifest, cancellationToken).ConfigureAwait(false);
            await ValidateReleaseAsync(
                    scope,
                    normalizedSnapshotId,
                    manifest.ContentSha256,
                    stagingRootPath,
                    cancellationToken)
                .ConfigureAwait(false);

            if (Directory.Exists(releaseRootPath) || File.Exists(releaseRootPath))
            {
                throw new IOException(
                    $"Project release {normalizedSnapshotId} already exists at '{releaseRootPath}' and is immutable.");
            }

            Directory.Move(stagingRootPath, releaseRootPath);

            return ToDescriptor(releaseRootPath, manifest);
        }
        finally
        {
            TryDeleteStagingDirectory(releasesPath, stagingRootPath);
        }
    }

    public async ValueTask<OpenedProjectReleaseArtifact?> OpenAsync(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        string expectedContentSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSnapshotId = RequireValue(snapshotId, nameof(snapshotId));
        ValidateSha256(expectedContentSha256, nameof(expectedContentSha256), argumentError: true);
        var releaseRootPath = ProjectReleaseArtifactPath.GetReleaseRootPath(scope, normalizedSnapshotId);
        if (!Directory.Exists(releaseRootPath))
        {
            if (File.Exists(releaseRootPath))
            {
                throw new InvalidDataException(
                    $"Project release path '{releaseRootPath}' is a file, not a release directory.");
            }

            return null;
        }

        return await ValidateReleaseAsync(
                scope,
                normalizedSnapshotId,
                expectedContentSha256,
                releaseRootPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask<OpenedProjectReleaseArtifact> IInstalledProjectReleaseReader.OpenAsync(
        string releaseRootPath,
        string expectedProjectId,
        string expectedApplicationId,
        string expectedSnapshotId,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(RequireValue(releaseRootPath, nameof(releaseRootPath)));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Installed Project release root '{root}' does not exist.");
        }

        var projectId = RequireValue(expectedProjectId, nameof(expectedProjectId));
        var applicationId = RequireValue(expectedApplicationId, nameof(expectedApplicationId));
        var snapshotId = RequireValue(expectedSnapshotId, nameof(expectedSnapshotId));
        var manifestPath = ProjectReleaseArtifactPath.GetManifestPath(root);
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var sourceRoot = ProjectReleaseArtifactPath.GetSourceRootPath(root);
        var scope = new ProjectApplicationWorkspaceScope(
            projectId,
            applicationId,
            sourceRoot,
            manifest.ApplicationProjectRelativePath);
        return await ValidateReleaseAsync(
                scope,
                snapshotId,
                manifest.ContentSha256,
                root,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RollbackPublicationAsync(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        string expectedContentSha256,
        CancellationToken cancellationToken = default)
    {
        var release = await OpenAsync(
                scope,
                snapshotId,
                expectedContentSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (release is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.Delete(release.ReleaseRootPath, recursive: true);
    }

    private static async ValueTask<ProjectReleaseSourceFile[]> CopySourceAsync(
        string sourceApplicationPath,
        string stagedApplicationPath,
        string sourceRootPath,
        FileTreeSnapshot sourceTree,
        CancellationToken cancellationToken)
    {
        foreach (var relativeDirectory in sourceTree.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(ProjectReleaseArtifactPath.ResolveRelativePath(
                stagedApplicationPath,
                relativeDirectory));
        }

        var copiedFiles = new List<ProjectReleaseSourceFile>(sourceTree.Files.Length);
        foreach (var relativeFile in sourceTree.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ProjectReleaseArtifactPath.ResolveRelativePath(sourceApplicationPath, relativeFile);
            var targetPath = ProjectReleaseArtifactPath.ResolveRelativePath(stagedApplicationPath, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            await using (var sourceStream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var targetStream = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await sourceStream.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
                await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            copiedFiles.Add(new ProjectReleaseSourceFile(
                ProjectReleaseArtifactPath.GetDocumentPath(sourceRootPath, targetPath),
                new FileInfo(targetPath).Length,
                await ComputeFileSha256Async(targetPath, cancellationToken).ConfigureAwait(false)));
        }

        return copiedFiles
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static async ValueTask CopyPackagesAsync(
        string releaseRootPath,
        IReadOnlyCollection<ProjectReleasePackageDependencyLock> dependencies,
        CancellationToken cancellationToken)
    {
        foreach (var dependency in dependencies
                     .GroupBy(item => item.PackageContentSha256, StringComparer.Ordinal)
                     .Select(group => group.First())
                     .OrderBy(item => item.PackageContentSha256, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dependency.SourcePackagePath))
            {
                throw new ArgumentException(
                    $"Package dependency {dependency.PackageId} does not contain a source package path.",
                    nameof(dependencies));
            }

            var sourcePackagePath = Path.GetFullPath(dependency.SourcePackagePath);
            var sourceTree = InspectFileTree(sourcePackagePath);
            var expectedFiles = dependency.Files
                .Select(file => file.RelativePath)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!sourceTree.Files.SequenceEqual(expectedFiles, StringComparer.Ordinal))
            {
                throw new IOException(
                    $"Plugin package {dependency.PackageId} changed before it could be frozen into the release.");
            }

            var targetPackagePath = ProjectReleaseArtifactPath.ResolveRelativePath(
                releaseRootPath,
                dependency.PackageRelativePath);
            Directory.CreateDirectory(targetPackagePath);
            foreach (var file in dependency.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = ProjectReleaseArtifactPath.ResolveRelativePath(sourcePackagePath, file.RelativePath);
                var targetPath = ProjectReleaseArtifactPath.ResolveRelativePath(targetPackagePath, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                await using (var sourceStream = new FileStream(
                                 sourcePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 FileBufferSize,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var targetStream = new FileStream(
                                 targetPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 FileBufferSize,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await sourceStream.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
                    await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                var targetInfo = new FileInfo(targetPath);
                var targetSha256 = await ComputeFileSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
                var sourceSha256 = await ComputeFileSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
                if (targetInfo.Length != file.SizeBytes
                    || !string.Equals(targetSha256, file.Sha256, StringComparison.Ordinal)
                    || !string.Equals(sourceSha256, file.Sha256, StringComparison.Ordinal))
                {
                    throw new IOException(
                        $"Plugin package {dependency.PackageId} file '{file.RelativePath}' changed while it was being frozen.");
                }
            }
        }
    }

    private static async ValueTask VerifySourceUnchangedAsync(
        string sourceApplicationPath,
        FileTreeSnapshot initialTree,
        FileTreeSnapshot releaseTree,
        IReadOnlyCollection<ProjectReleaseSourceFile> copiedFiles,
        string sourceApplicationRelativePath,
        CancellationToken cancellationToken)
    {
        var currentTree = InspectFileTree(sourceApplicationPath);
        if (!initialTree.Directories.SequenceEqual(currentTree.Directories, StringComparer.Ordinal)
            || !initialTree.Files.SequenceEqual(currentTree.Files, StringComparer.Ordinal))
        {
            throw new IOException("Project application source changed while the release was being published.");
        }

        var copiedByPath = copiedFiles.ToDictionary(
            file => file.RelativePath,
            StringComparer.Ordinal);
        foreach (var relativeFile in releaseTree.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ProjectReleaseArtifactPath.ResolveRelativePath(sourceApplicationPath, relativeFile);
            var releaseRelativePath = $"{sourceApplicationRelativePath}/{relativeFile}";
            if (!copiedByPath.TryGetValue(releaseRelativePath, out var copiedFile))
            {
                throw new IOException("Project application source changed while the release was being published.");
            }

            var sourceInfo = new FileInfo(sourcePath);
            var sourceSha256 = await ComputeFileSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
            if (sourceInfo.Length != copiedFile.SizeBytes
                || !string.Equals(sourceSha256, copiedFile.Sha256, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Project application source file '{relativeFile}' changed while the release was being published.");
            }
        }
    }

    private static FileTreeSnapshot FilterReleaseSourceTree(
        FileTreeSnapshot sourceTree,
        ProjectReleaseSourceMetadata metadata)
    {
        var includedResourcePrefixes = metadata.ExternalProgramResources
            .Select(resource => resource.ResourceRelativePath + "/")
            .ToArray();
        var files = sourceTree.Files.Where(path =>
                !path.StartsWith(
                    ExternalProgramResourceContract.ResourceDirectoryName + "/",
                    StringComparison.Ordinal)
                || includedResourcePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();
        var directories = sourceTree.Directories.Where(path =>
                !path.StartsWith(
                    ExternalProgramResourceContract.ResourceDirectoryName + "/",
                    StringComparison.Ordinal)
                || string.Equals(
                    path,
                    ExternalProgramResourceContract.ResourceDirectoryName,
                    StringComparison.Ordinal)
                || includedResourcePrefixes.Any(prefix =>
                    path.StartsWith(prefix.TrimEnd('/'), StringComparison.Ordinal)
                    || prefix.StartsWith(path + "/", StringComparison.Ordinal)))
            .ToArray();
        return new FileTreeSnapshot(directories, files);
    }

    private static async ValueTask<OpenedProjectReleaseArtifact> ValidateReleaseAsync(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        string expectedContentSha256,
        string releaseRootPath,
        CancellationToken cancellationToken)
    {
        var manifestPath = ProjectReleaseArtifactPath.GetManifestPath(releaseRootPath);
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        ValidateManifestIdentity(scope, snapshotId, manifest, manifestPath, releaseRootPath);

        ProjectReleaseSourceMetadata normalizedMetadata;
        try
        {
            normalizedMetadata = NormalizeMetadata(manifest.Metadata);
        }
        catch (ArgumentException exception)
        {
            throw InvalidRelease(manifestPath, "contains invalid source metadata", exception);
        }

        if (!CanonicalJsonEquals(manifest.Metadata, normalizedMetadata))
        {
            throw InvalidRelease(manifestPath, "source metadata is not normalized and deterministically ordered");
        }

        ValidateManifestFiles(manifestPath, manifest.Files, manifest.SourceApplicationRelativePath);
        ValidateSha256(manifest.ContentSha256, nameof(manifest.ContentSha256), argumentError: false);
        var computedContentSha256 = ComputeContentSha256(manifest);
        if (!string.Equals(manifest.ContentSha256, computedContentSha256, StringComparison.Ordinal))
        {
            throw InvalidRelease(manifestPath, "content SHA-256 does not match release.json");
        }

        if (!string.Equals(manifest.ContentSha256, expectedContentSha256, StringComparison.Ordinal))
        {
            throw InvalidRelease(
                manifestPath,
                $"content SHA-256 is {manifest.ContentSha256}, expected {expectedContentSha256}");
        }

        var releaseTree = InspectFileTree(releaseRootPath);
        var expectedReleaseFiles = manifest.Files
            .Select(file => $"source/{file.RelativePath}")
            .Concat(normalizedMetadata.PackageDependencies.SelectMany(dependency =>
                dependency.Files.Select(file => $"{dependency.PackageRelativePath}/{file.RelativePath}")))
            .Append(ProjectReleaseManifest.FileName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!releaseTree.Files.SequenceEqual(expectedReleaseFiles, StringComparer.Ordinal))
        {
            var missing = expectedReleaseFiles.Except(releaseTree.Files, StringComparer.Ordinal).ToArray();
            var extra = releaseTree.Files.Except(expectedReleaseFiles, StringComparer.Ordinal).ToArray();
            throw InvalidRelease(
                manifestPath,
                $"file set differs (missing: {FormatPaths(missing)}; extra: {FormatPaths(extra)})");
        }

        var sourceRootPath = ProjectReleaseArtifactPath.GetSourceRootPath(releaseRootPath);
        var sourceApplicationPath = ProjectReleaseArtifactPath.ResolveRelativePath(
            sourceRootPath,
            manifest.SourceApplicationRelativePath);
        if (!Directory.Exists(sourceRootPath) || !Directory.Exists(sourceApplicationPath))
        {
            throw InvalidRelease(manifestPath, "source application directory is missing");
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ProjectReleaseArtifactPath.ResolveRelativePath(sourceRootPath, file.RelativePath);
            if (!File.Exists(path))
            {
                throw InvalidRelease(manifestPath, $"source file '{file.RelativePath}' is missing");
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length != file.SizeBytes)
            {
                throw InvalidRelease(
                    manifestPath,
                    $"source file '{file.RelativePath}' size is {fileInfo.Length}, expected {file.SizeBytes}");
            }

            var actualSha256 = await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualSha256, file.Sha256, StringComparison.Ordinal))
            {
                throw InvalidRelease(manifestPath, $"source file '{file.RelativePath}' SHA-256 does not match");
            }
        }

        await ValidateEmbeddedApplicationSchemasAsync(
                sourceApplicationPath,
                manifest.ApplicationId,
                normalizedMetadata,
                manifestPath,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var dependency in normalizedMetadata.PackageDependencies)
        {
            var packageRootPath = ProjectReleaseArtifactPath.ResolveRelativePath(
                releaseRootPath,
                dependency.PackageRelativePath);
            if (!Directory.Exists(packageRootPath))
            {
                throw InvalidRelease(
                    manifestPath,
                    $"plugin package {dependency.PackageId} content directory is missing");
            }

            foreach (var file in dependency.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ProjectReleaseArtifactPath.ResolveRelativePath(packageRootPath, file.RelativePath);
                if (!File.Exists(path))
                {
                    throw InvalidRelease(
                        manifestPath,
                        $"plugin package {dependency.PackageId} file '{file.RelativePath}' is missing");
                }

                var fileInfo = new FileInfo(path);
                var actualSha256 = await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false);
                if (fileInfo.Length != file.SizeBytes
                    || !string.Equals(actualSha256, file.Sha256, StringComparison.Ordinal))
                {
                    throw InvalidRelease(
                        manifestPath,
                        $"plugin package {dependency.PackageId} file '{file.RelativePath}' integrity does not match");
                }
            }
        }

        return new OpenedProjectReleaseArtifact(
            manifest.SnapshotId,
            manifest.ProjectId,
            manifest.ApplicationId,
            manifest.PublishedAtUtc,
            manifest.ContentSha256,
            releaseRootPath,
            sourceRootPath,
            manifest.ApplicationProjectRelativePath,
            manifestPath,
            normalizedMetadata,
            manifest.Files);
    }

    private static async ValueTask ValidateEmbeddedApplicationSchemasAsync(
        string sourceApplicationPath,
        string applicationId,
        ProjectReleaseSourceMetadata metadata,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var topologyDirectory = Path.Combine(sourceApplicationPath, "topology");
        var layoutDirectory = Path.Combine(sourceApplicationPath, "layouts");
        var productionLinesDirectory = Path.Combine(sourceApplicationPath, "production", "lines");
        if (!Directory.Exists(topologyDirectory)
            || !Directory.Exists(layoutDirectory)
            || !Directory.Exists(productionLinesDirectory))
        {
            throw InvalidRelease(
                manifestPath,
                "embedded topology, layout, or Production line directory is missing");
        }

        var topologyIds = new List<string>();
        foreach (var path in Directory.EnumerateFiles(topologyDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var identity = await ReadEmbeddedResourceIdentityAsync(
                    path,
                    identityPropertyName: "topologyId",
                    forbiddenIdentityPropertyName: "layoutId",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(identity.SchemaVersion, ApplicationResourceSchemaVersions.AutomationTopology, StringComparison.Ordinal)
                || !string.Equals(identity.ResourceKind, "OpenLineOps.AutomationTopology", StringComparison.Ordinal)
                || !string.Equals(identity.ApplicationId, applicationId, StringComparison.Ordinal))
            {
                throw InvalidRelease(manifestPath, $"embedded topology '{Path.GetFileName(path)}' has an unsupported schema or identity");
            }

            topologyIds.Add(identity.ResourceId);
        }

        if (topologyIds.Count(id => string.Equals(id, metadata.TopologyId, StringComparison.Ordinal)) != 1)
        {
            throw InvalidRelease(manifestPath, $"embedded topology {metadata.TopologyId} is missing or duplicated");
        }

        var layoutIds = new List<string>();
        foreach (var path in Directory.EnumerateFiles(layoutDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var identity = await ReadEmbeddedResourceIdentityAsync(
                    path,
                    identityPropertyName: "layoutId",
                    forbiddenIdentityPropertyName: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(identity.SchemaVersion, ApplicationResourceSchemaVersions.SiteLayout, StringComparison.Ordinal)
                || !string.Equals(identity.ResourceKind, "OpenLineOps.SiteLayout", StringComparison.Ordinal)
                || !string.Equals(identity.ApplicationId, applicationId, StringComparison.Ordinal))
            {
                throw InvalidRelease(manifestPath, $"embedded layout '{Path.GetFileName(path)}' has an unsupported schema or identity");
            }

            layoutIds.Add(identity.ResourceId);
        }

        if (metadata.LayoutIds.Any(layoutId =>
                layoutIds.Count(candidate => string.Equals(candidate, layoutId, StringComparison.Ordinal)) != 1))
        {
            throw InvalidRelease(manifestPath, "one or more frozen layout identities are missing or duplicated");
        }

        var productionLineIds = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     productionLinesDirectory,
                     "line.json",
                     SearchOption.AllDirectories))
        {
            var identity = await ReadEmbeddedResourceIdentityAsync(
                    path,
                    identityPropertyName: "lineDefinitionId",
                    forbiddenIdentityPropertyName: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    identity.SchemaVersion,
                    ApplicationResourceSchemaVersions.ProductionLine,
                    StringComparison.Ordinal)
                || !string.Equals(identity.ResourceKind, "OpenLineOps.ProductionLine", StringComparison.Ordinal)
                || !string.Equals(identity.ApplicationId, applicationId, StringComparison.Ordinal))
            {
                throw InvalidRelease(
                    manifestPath,
                    $"embedded Production line '{Path.GetFileName(Path.GetDirectoryName(path))}' has an unsupported schema or identity");
            }

            productionLineIds.Add(identity.ResourceId);
        }

        if (productionLineIds.Count(id => string.Equals(
                id,
                metadata.ProductionLine.LineDefinitionId,
                StringComparison.Ordinal)) != 1)
        {
            throw InvalidRelease(
                manifestPath,
                $"embedded Production line {metadata.ProductionLine.LineDefinitionId} is missing or duplicated");
        }

        await ValidateEmbeddedExternalProgramsAsync(
                sourceApplicationPath,
                metadata.ExternalProgramResources,
                manifestPath,
                cancellationToken)
            .ConfigureAwait(false);

        await ValidateEmbeddedOperationConfigurationsAsync(
                sourceApplicationPath,
                applicationId,
                metadata,
                manifestPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask ValidateEmbeddedExternalProgramsAsync(
        string sourceApplicationPath,
        IReadOnlyCollection<ProjectReleaseExternalProgramResource> resources,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        foreach (var resource in resources)
        {
            var resourceDirectory = ProjectReleaseArtifactPath.ResolveRelativePath(
                sourceApplicationPath,
                resource.ResourceRelativePath);
            var descriptorPath = ProjectReleaseArtifactPath.ResolveRelativePath(
                resourceDirectory,
                ExternalProgramResourceContract.DescriptorFileName);
            if (!Directory.Exists(resourceDirectory) || !File.Exists(descriptorPath))
            {
                throw InvalidRelease(
                    manifestPath,
                    $"embedded external program resource {resource.ResourceId} is missing");
            }

            var actualTree = InspectFileTree(resourceDirectory);
            var expectedFiles = resource.Files
                .Select(file => file.RelativePath[(resource.ResourceRelativePath.Length + 1)..])
                .Append(ExternalProgramResourceContract.DescriptorFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!actualTree.Files.SequenceEqual(expectedFiles, StringComparer.Ordinal))
            {
                throw InvalidRelease(
                    manifestPath,
                    $"embedded external program resource {resource.ResourceId} file set differs from its frozen inventory");
            }

            foreach (var file in resource.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ProjectReleaseArtifactPath.ResolveRelativePath(
                    sourceApplicationPath,
                    file.RelativePath);
                var info = new FileInfo(path);
                var hash = await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false);
                if (!info.Exists
                    || info.Length != file.SizeBytes
                    || !string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                {
                    throw InvalidRelease(
                        manifestPath,
                        $"embedded external program resource {resource.ResourceId} file '{file.RelativePath}' failed SHA-256 validation");
                }
            }
        }
    }

    private static async ValueTask ValidateEmbeddedOperationConfigurationsAsync(
        string sourceApplicationPath,
        string applicationId,
        ProjectReleaseSourceMetadata metadata,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var projectsDirectory = Path.Combine(sourceApplicationPath, "configuration", "projects");
        var stationProfilesDirectory = Path.Combine(
            sourceApplicationPath,
            "configuration",
            "station-profiles");
        if (!Directory.Exists(projectsDirectory) || !Directory.Exists(stationProfilesDirectory))
        {
            throw InvalidRelease(
                manifestPath,
                "embedded engineering project or Station profile directory is missing");
        }

        var snapshots = new List<EmbeddedConfigurationSnapshot>();
        foreach (var path in Directory.EnumerateFiles(
                     projectsDirectory,
                     "project-*.json",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            ValidateEmbeddedEngineeringResource(
                root,
                applicationId,
                expectedKind: "project",
                path,
                manifestPath);
            var projectSnapshot = GetRequiredEmbeddedObject(
                root,
                "snapshot",
                path,
                manifestPath);
            var configurationSnapshots = GetRequiredEmbeddedArray(
                projectSnapshot,
                "snapshots",
                path,
                manifestPath);
            foreach (var configurationSnapshot in configurationSnapshots.EnumerateArray())
            {
                snapshots.Add(new EmbeddedConfigurationSnapshot(
                    GetRequiredEmbeddedString(
                        configurationSnapshot,
                        "snapshotId",
                        path,
                        manifestPath),
                    GetRequiredEmbeddedString(
                        configurationSnapshot,
                        "processDefinitionId",
                        path,
                        manifestPath),
                    GetRequiredEmbeddedString(
                        configurationSnapshot,
                        "processVersionId",
                        path,
                        manifestPath),
                    GetRequiredEmbeddedString(
                        configurationSnapshot,
                        "stationProfileId",
                        path,
                        manifestPath),
                    GetRequiredEmbeddedString(
                        configurationSnapshot,
                        "status",
                        path,
                        manifestPath)));
            }
        }

        var stationProfiles = new List<EmbeddedStationProfile>();
        foreach (var path in Directory.EnumerateFiles(
                     stationProfilesDirectory,
                     "station-profile-*.json",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            ValidateEmbeddedEngineeringResource(
                root,
                applicationId,
                expectedKind: "station-profile",
                path,
                manifestPath);
            var stationProfile = GetRequiredEmbeddedObject(
                root,
                "snapshot",
                path,
                manifestPath);
            stationProfiles.Add(new EmbeddedStationProfile(
                GetRequiredEmbeddedString(
                    stationProfile,
                    "stationProfileId",
                    path,
                    manifestPath),
                GetRequiredEmbeddedString(
                    stationProfile,
                    "stationSystemId",
                    path,
                    manifestPath)));
        }

        foreach (var operation in metadata.ProductionLine.Operations)
        {
            var matchingSnapshots = snapshots.Where(snapshot => string.Equals(
                    snapshot.SnapshotId,
                    operation.ConfigurationSnapshotId,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matchingSnapshots.Length != 1)
            {
                throw InvalidRelease(
                    manifestPath,
                    $"Production operation {operation.OperationId} configuration snapshot {operation.ConfigurationSnapshotId} is missing or duplicated");
            }

            var snapshot = matchingSnapshots[0];
            if (!string.Equals(snapshot.Status, "Published", StringComparison.Ordinal)
                || !string.Equals(
                    snapshot.ProcessDefinitionId,
                    operation.FlowDefinitionId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    snapshot.ProcessVersionId,
                    operation.FlowVersionId,
                    StringComparison.Ordinal))
            {
                throw InvalidRelease(
                    manifestPath,
                    $"Production operation {operation.OperationId} configuration snapshot does not match its frozen Flow");
            }

            var matchingProfiles = stationProfiles.Where(profile => string.Equals(
                    profile.StationProfileId,
                    snapshot.StationProfileId,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matchingProfiles.Length != 1
                || !string.Equals(
                    matchingProfiles[0].StationSystemId,
                    operation.StationSystemId,
                    StringComparison.Ordinal))
            {
                throw InvalidRelease(
                    manifestPath,
                    $"Production operation {operation.OperationId} configuration Station does not match its frozen Station System");
            }
        }
    }

    private static void ValidateEmbeddedEngineeringResource(
        JsonElement root,
        string applicationId,
        string expectedKind,
        string path,
        string manifestPath)
    {
        if (!string.Equals(
                GetRequiredEmbeddedString(root, "schema", path, manifestPath),
                "openlineops.engineering-configuration-resource",
                StringComparison.Ordinal)
            || !root.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.ValueKind != JsonValueKind.Number
            || !schemaVersion.TryGetInt32(out var version)
            || version != 1
            || !string.Equals(
                GetRequiredEmbeddedString(root, "applicationId", path, manifestPath),
                applicationId,
                StringComparison.Ordinal)
            || !string.Equals(
                GetRequiredEmbeddedString(root, "resourceKind", path, manifestPath),
                expectedKind,
                StringComparison.Ordinal))
        {
            throw InvalidRelease(
                manifestPath,
                $"embedded engineering resource '{Path.GetFileName(path)}' has an unsupported schema or identity");
        }
    }

    private static JsonElement GetRequiredEmbeddedObject(
        JsonElement root,
        string propertyName,
        string path,
        string manifestPath)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidRelease(
                manifestPath,
                $"embedded resource '{Path.GetFileName(path)}' has no valid '{propertyName}' object");
        }

        return value;
    }

    private static JsonElement GetRequiredEmbeddedArray(
        JsonElement root,
        string propertyName,
        string path,
        string manifestPath)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidRelease(
                manifestPath,
                $"embedded resource '{Path.GetFileName(path)}' has no valid '{propertyName}' array");
        }

        return value;
    }

    private static string GetRequiredEmbeddedString(
        JsonElement root,
        string propertyName,
        string path,
        string manifestPath)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || !string.Equals(value.GetString(), value.GetString()!.Trim(), StringComparison.Ordinal))
        {
            throw InvalidRelease(
                manifestPath,
                $"embedded resource '{Path.GetFileName(path)}' has no valid '{propertyName}'");
        }

        return value.GetString()!;
    }

    private static async ValueTask<(string SchemaVersion, string ResourceKind, string ApplicationId, string ResourceId)>
        ReadEmbeddedResourceIdentityAsync(
            string path,
            string identityPropertyName,
            string? forbiddenIdentityPropertyName,
            CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (forbiddenIdentityPropertyName is not null
                && root.TryGetProperty(forbiddenIdentityPropertyName, out _))
            {
                throw new InvalidDataException(
                    $"Embedded application resource '{path}' contains forbidden identity '{forbiddenIdentityPropertyName}'.");
            }

            return (
                RequireJsonString(root, "schemaVersion", path),
                RequireJsonString(root, "resourceKind", path),
                RequireJsonString(root, "applicationId", path),
                RequireJsonString(root, identityPropertyName, path));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Embedded application resource '{path}' is invalid JSON.", exception);
        }
    }

    private static string RequireJsonString(JsonElement root, string propertyName, string path)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()!
                : throw new InvalidDataException(
                    $"Embedded application resource '{path}' has no valid '{propertyName}'.");
    }

    private static void ValidateManifestIdentity(
        ProjectApplicationWorkspaceScope scope,
        string snapshotId,
        ProjectReleaseManifest manifest,
        string manifestPath,
        string releaseRootPath)
    {
        if (!string.Equals(
                manifest.Schema,
                ProjectReleaseManifest.RequiredSchema,
                StringComparison.Ordinal))
        {
            throw InvalidRelease(manifestPath, $"schema '{manifest.Schema}' is not supported");
        }

        if (manifest.SchemaVersion != ProjectReleaseManifest.RequiredSchemaVersion)
        {
            throw InvalidRelease(manifestPath, $"schema version {manifest.SchemaVersion} is not supported");
        }

        if (!string.Equals(manifest.SnapshotId, snapshotId, StringComparison.Ordinal))
        {
            throw InvalidRelease(
                manifestPath,
                $"snapshot id is {manifest.SnapshotId}, expected {snapshotId}");
        }

        if (!string.Equals(manifest.ProjectId, scope.ProjectId, StringComparison.Ordinal)
            || !string.Equals(manifest.ApplicationId, scope.ApplicationId, StringComparison.Ordinal))
        {
            throw InvalidRelease(
                manifestPath,
                $"scope is {manifest.ProjectId}/{manifest.ApplicationId}, expected {scope.ProjectId}/{scope.ApplicationId}");
        }

        ProjectApplicationWorkspaceScope releaseScope;
        try
        {
            releaseScope = new ProjectApplicationWorkspaceScope(
                manifest.ProjectId,
                manifest.ApplicationId,
                ProjectReleaseArtifactPath.GetSourceRootPath(releaseRootPath),
                manifest.ApplicationProjectRelativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            throw InvalidRelease(manifestPath, "contains an invalid Application project path", exception);
        }

        var expectedApplicationPath = ProjectReleaseArtifactPath
            .GetSourceApplicationRelativePath(releaseScope);
        if (!string.Equals(
                manifest.SourceApplicationRelativePath,
                expectedApplicationPath,
                StringComparison.Ordinal))
        {
            throw InvalidRelease(
                manifestPath,
                $"source application path is '{manifest.SourceApplicationRelativePath}', expected '{expectedApplicationPath}'");
        }
    }

    private static void ValidateManifestFiles(
        string manifestPath,
        ProjectReleaseSourceFile[]? files,
        string sourceApplicationRelativePath)
    {
        if (files is null)
        {
            throw InvalidRelease(manifestPath, "has no source files collection");
        }

        var expectedPrefix = sourceApplicationRelativePath + "/";
        var releaseRootPath = Path.GetDirectoryName(manifestPath)!;
        var sourceRootPath = ProjectReleaseArtifactPath.GetSourceRootPath(releaseRootPath);
        var sourceApplicationPath = ProjectReleaseArtifactPath.ResolveRelativePath(
            sourceRootPath,
            sourceApplicationRelativePath);
        var sourceApplicationPrefix = Path.GetFullPath(sourceApplicationPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        foreach (var file in files)
        {
            if (file is null)
            {
                throw InvalidRelease(manifestPath, "contains an empty source file entry");
            }

            if (string.IsNullOrWhiteSpace(file.RelativePath)
                || !string.Equals(file.RelativePath, file.RelativePath.Trim(), StringComparison.Ordinal)
                || file.RelativePath.Contains('\\', StringComparison.Ordinal)
                || !file.RelativePath.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                throw InvalidRelease(
                    manifestPath,
                    $"source file path '{file.RelativePath}' is not canonical or is outside the application source");
            }

            var resolvedPath = ProjectReleaseArtifactPath.ResolveRelativePath(sourceRootPath, file.RelativePath);
            var canonicalPath = ProjectReleaseArtifactPath.GetDocumentPath(sourceRootPath, resolvedPath);
            if (!resolvedPath.StartsWith(sourceApplicationPrefix, PathComparison)
                || !string.Equals(canonicalPath, file.RelativePath, StringComparison.Ordinal))
            {
                throw InvalidRelease(
                    manifestPath,
                    $"source file path '{file.RelativePath}' is not canonical or is outside the application source");
            }

            if (!paths.Add(file.RelativePath))
            {
                throw InvalidRelease(manifestPath, $"source file path '{file.RelativePath}' is duplicated");
            }

            if (previousPath is not null
                && StringComparer.Ordinal.Compare(previousPath, file.RelativePath) >= 0)
            {
                throw InvalidRelease(manifestPath, "source file entries are not deterministically ordered");
            }

            if (file.SizeBytes < 0)
            {
                throw InvalidRelease(manifestPath, $"source file '{file.RelativePath}' has a negative size");
            }

            ValidateSha256(file.Sha256, $"source file '{file.RelativePath}' SHA-256", argumentError: false);
            if (!string.Equals(file.Sha256, file.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw InvalidRelease(manifestPath, $"source file '{file.RelativePath}' SHA-256 is not lowercase");
            }

            previousPath = file.RelativePath;
        }
    }

    private static ProjectReleaseSourceMetadata NormalizeMetadata(ProjectReleaseSourceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var topologyId = RequireValue(metadata.TopologyId, nameof(metadata.TopologyId));
        var layoutIds = NormalizeIdentifiers(metadata.LayoutIds, nameof(metadata.LayoutIds));
        var blockVersionIds = NormalizeIdentifiers(metadata.BlockVersionIds, nameof(metadata.BlockVersionIds));
        var productionLine = NormalizeProductionLine(metadata.ProductionLine);
        var externalPrograms = NormalizeExternalPrograms(metadata.ExternalProgramResources);
        var packageDependencies = NormalizePackageDependencies(metadata.PackageDependencies);
        var normalizedCapabilityBindings = (metadata.CapabilityBindings
                ?? throw new ArgumentException("CapabilityBindings collection is required.", nameof(metadata)))
            .Select(binding => binding is null
                ? throw new ArgumentException("CapabilityBindings cannot contain null.", nameof(metadata))
                : new ProjectReleaseCapabilityBinding(
                    RequireValue(binding.CapabilityId, nameof(binding.CapabilityId)),
                    RequireValue(binding.BindingId, nameof(binding.BindingId)),
                    RequireValue(binding.ProviderKind, nameof(binding.ProviderKind)),
                    RequireValue(binding.ProviderKey, nameof(binding.ProviderKey)),
                    RequireValue(binding.OwnerSystemId, nameof(binding.OwnerSystemId)),
                    RequireValue(binding.OwnerStationSystemId, nameof(binding.OwnerStationSystemId))))
            .ToArray();
        if (normalizedCapabilityBindings.Distinct().Count() != normalizedCapabilityBindings.Length)
        {
            throw new ArgumentException(
                "CapabilityBindings cannot contain duplicate frozen identities.",
                nameof(metadata));
        }

        var capabilityBindings = normalizedCapabilityBindings
            .OrderBy(binding => binding.OwnerSystemId, StringComparer.Ordinal)
            .ThenBy(binding => binding.CapabilityId, StringComparer.Ordinal)
            .ThenBy(binding => binding.BindingId, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.ProviderKey, StringComparer.Ordinal)
            .ThenBy(binding => binding.OwnerStationSystemId, StringComparer.Ordinal)
            .ToArray();
        var normalizedTargetReferences = (metadata.TargetReferences
                ?? throw new ArgumentException("TargetReferences collection is required.", nameof(metadata)))
            .Select(target => target is null
                ? throw new ArgumentException("TargetReferences cannot contain null.", nameof(metadata))
                : new ProjectReleaseTargetReference(
                    RequireValue(target.Kind, nameof(target.Kind)),
                    RequireValue(target.TargetId, nameof(target.TargetId))))
            .ToArray();
        if (normalizedTargetReferences.Distinct().Count() != normalizedTargetReferences.Length)
        {
            throw new ArgumentException(
                "TargetReferences cannot contain duplicate frozen identities.",
                nameof(metadata));
        }

        var targetReferences = normalizedTargetReferences
            .OrderBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.TargetId, StringComparer.Ordinal)
            .ToArray();
        if (capabilityBindings
                .GroupBy(
                    binding => $"{binding.OwnerSystemId}\u001f{binding.CapabilityId}",
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1)
            || capabilityBindings.Select(binding => binding.BindingId)
                .Distinct(StringComparer.Ordinal).Count() != capabilityBindings.Length
            || capabilityBindings.Select(binding => binding.BindingId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != capabilityBindings.Length
            || capabilityBindings.Any(binding => targetReferences.Count(target =>
                string.Equals(target.Kind, "System", StringComparison.Ordinal)
                && string.Equals(
                    target.TargetId,
                    binding.OwnerSystemId,
                    StringComparison.Ordinal)) != 1
                || targetReferences.Count(target =>
                    string.Equals(target.Kind, "System", StringComparison.Ordinal)
                    && string.Equals(
                        target.TargetId,
                        binding.OwnerStationSystemId,
                        StringComparison.Ordinal)) != 1))
        {
            throw new ArgumentException(
                "Capability bindings must have unique Binding ids, be unique per owning System, and reference one frozen System target.",
                nameof(metadata));
        }

        if (!string.Equals(productionLine.TopologyId, topologyId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Production line topology must match release topology.",
                nameof(metadata));
        }
        if (productionLine.Operations.Any(operation => targetReferences.Count(target =>
                string.Equals(target.Kind, "System", StringComparison.Ordinal)
                && string.Equals(
                    target.TargetId,
                    operation.StationSystemId,
                    StringComparison.Ordinal)) != 1))
        {
            throw new ArgumentException(
                "Every frozen Production operation Station must match exactly one System target reference.",
                nameof(metadata));
        }

        foreach (var authorization in productionLine.LineControllerAuthorizations)
        {
            var operation = productionLine.Operations.Single(candidate => string.Equals(
                candidate.OperationId,
                authorization.OperationId,
                StringComparison.Ordinal));
            if (string.Equals(
                    operation.StationSystemId,
                    authorization.TargetStationSystemId,
                    StringComparison.Ordinal)
                || capabilityBindings.Count(binding =>
                    string.Equals(
                        binding.BindingId,
                        authorization.ControllerBindingId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        binding.OwnerSystemId,
                        authorization.ControllerSystemId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        binding.OwnerStationSystemId,
                        operation.StationSystemId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        binding.CapabilityId,
                        authorization.ControllerCapabilityId,
                        StringComparison.Ordinal)) != 1
                || capabilityBindings.Count(binding =>
                    string.Equals(
                        binding.BindingId,
                        authorization.TargetBindingId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        binding.OwnerSystemId,
                        authorization.TargetSystemId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        binding.OwnerStationSystemId,
                        authorization.TargetStationSystemId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        binding.CapabilityId,
                        authorization.TargetCapabilityId,
                        StringComparison.Ordinal)) != 1
                || targetReferences.Count(target => string.Equals(
                        target.Kind,
                        "Driver",
                        StringComparison.Ordinal)
                    && string.Equals(
                        target.TargetId,
                        authorization.TargetBindingId,
                        StringComparison.Ordinal)) != 1)
            {
                throw new ArgumentException(
                    $"Line Controller authorization {authorization.AuthorizationId} does not match exact frozen controller and remote target binding identities.",
                    nameof(metadata));
            }
        }

        var productionBlockVersionIds = productionLine.Operations
            .SelectMany(operation => operation.BlockVersionIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!blockVersionIds.SequenceEqual(productionBlockVersionIds, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Release block version locks must equal the union of frozen Production operation block locks.",
                nameof(metadata));
        }
        if (packageDependencies.Any(dependency => capabilityBindings.Count(binding =>
                string.Equals(dependency.CapabilityId, binding.CapabilityId, StringComparison.Ordinal)
                && string.Equals(dependency.BindingId, binding.BindingId, StringComparison.Ordinal)
                && string.Equals(dependency.ProviderKind, binding.ProviderKind, StringComparison.Ordinal)
                && string.Equals(dependency.ProviderKey, binding.ProviderKey, StringComparison.Ordinal)
                && string.Equals(dependency.OwnerSystemId, binding.OwnerSystemId, StringComparison.Ordinal)
                && string.Equals(
                    dependency.OwnerStationSystemId,
                    binding.OwnerStationSystemId,
                    StringComparison.Ordinal)) != 1))
        {
            throw new ArgumentException(
                "Every package dependency lock must match exactly one capability binding.",
                nameof(metadata));
        }

        return new ProjectReleaseSourceMetadata(
            topologyId,
            layoutIds,
            productionLine,
            externalPrograms,
            capabilityBindings,
            targetReferences,
            blockVersionIds,
            packageDependencies);
    }

    private static ProjectReleaseProductionLine NormalizeProductionLine(
        ProjectReleaseProductionLine? line)
    {
        if (line is null)
        {
            throw new ArgumentException("ProductionLine is required.", nameof(line));
        }

        if (line.ProductModel is null)
        {
            throw new ArgumentException("ProductionLine.ProductModel is required.", nameof(line));
        }

        var productModel = new ProjectReleaseProductModel(
            RequireProductionValue(
                line.ProductModel.ProductModelId,
                nameof(line.ProductModel.ProductModelId)),
            RequireProductionValue(line.ProductModel.ModelCode, nameof(line.ProductModel.ModelCode)),
            RequireProductionValue(
                line.ProductModel.IdentityInputKey,
                nameof(line.ProductModel.IdentityInputKey)));
        var operations = (line.Operations
                ?? throw new ArgumentException("ProductionLine.Operations is required.", nameof(line)))
            .Select(operation =>
            {
                if (operation is null)
                {
                    throw new ArgumentException(
                        "ProductionLine.Operations cannot contain null.",
                        nameof(line));
                }

                var flowIr = NormalizeFrozenFlowIr(
                    operation.FlowIrSchema,
                    operation.FlowIrSha256,
                    operation.FlowIrCanonicalJson,
                    $"Production operation {operation.OperationId} Flow IR");
                var resources = NormalizeOperationResources(
                    operation.Resources,
                    operation.StationSystemId,
                    operation.OperationId);
                var authorizedActions = NormalizeAuthorizedActions(
                    operation.AuthorizedActions,
                    operation.OperationId);
                return new ProjectReleaseOperation(
                    RequireProductionValue(operation.OperationId, nameof(operation.OperationId)),
                    RequireProductionValue(operation.DisplayName, nameof(operation.DisplayName)),
                    RequireProductionValue(
                        operation.StationSystemId,
                        nameof(operation.StationSystemId)),
                    RequireProductionValue(
                        operation.FlowDefinitionId,
                        nameof(operation.FlowDefinitionId)),
                    RequireProductionValue(
                        operation.ConfigurationSnapshotId,
                        nameof(operation.ConfigurationSnapshotId)),
                    RequireProductionValue(
                        operation.FlowVersionId,
                        nameof(operation.FlowVersionId)),
                    flowIr.SchemaVersion,
                    flowIr.Sha256,
                    flowIr.CanonicalJson,
                    NormalizeIdentifiers(
                        operation.BlockVersionIds,
                        nameof(operation.BlockVersionIds)),
                    resources,
                    authorizedActions);
            })
            .OrderBy(operation => operation.OperationId, StringComparer.Ordinal)
            .ToArray();
        if (operations.Length == 0)
        {
            throw new ArgumentException("ProductionLine requires at least one Operation.", nameof(line));
        }

        EnsureProductionIdentifiersAreUnique(
            operations.Select(operation => operation.OperationId),
            "Production Operation ids");
        var entryOperationId = RequireProductionValue(
            line.EntryOperationId,
            nameof(line.EntryOperationId));
        if (operations.Count(operation => string.Equals(
                operation.OperationId,
                entryOperationId,
                StringComparison.Ordinal)) != 1)
        {
            throw new ArgumentException(
                $"Production entry Operation {entryOperationId} must exist exactly once.",
                nameof(line));
        }

        var operationIds = operations
            .Select(operation => operation.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var transitions = (line.Transitions
                ?? throw new ArgumentException("ProductionLine.Transitions is required.", nameof(line)))
            .Select(transition => NormalizeRouteTransition(transition, operationIds))
            .OrderBy(transition => transition.TransitionId, StringComparer.Ordinal)
            .ToArray();
        EnsureProductionIdentifiersAreUnique(
            transitions.Select(transition => transition.TransitionId),
            "Production Route Transition ids");
        ValidateRouteGraph(entryOperationId, operations, transitions);

        var lineControllerAuthorizations = NormalizeLineControllerAuthorizations(
            line.LineControllerAuthorizations,
            operations);

        return new ProjectReleaseProductionLine(
            RequireProductionValue(line.LineDefinitionId, nameof(line.LineDefinitionId)),
            RequireProductionValue(line.DisplayName, nameof(line.DisplayName)),
            RequireProductionValue(line.TopologyId, nameof(line.TopologyId)),
            productModel,
            entryOperationId,
            operations,
            transitions,
            lineControllerAuthorizations);
    }

    private static ProjectReleaseOperationResource[] NormalizeOperationResources(
        IReadOnlyCollection<ProjectReleaseOperationResource>? resources,
        string stationSystemId,
        string operationId)
    {
        var normalized = (resources
                ?? throw new ArgumentException(
                    $"Production operation {operationId} resources are required.",
                    nameof(resources)))
            .Select(resource =>
            {
                if (resource is null)
                {
                    throw new ArgumentException(
                        $"Production operation {operationId} resources cannot contain null.",
                        nameof(resources));
                }

                var kind = RequireExactToken(
                    resource.Kind,
                    ["Station", "Fixture", "Device", "SlotGroup", "Slot"],
                    "Operation resource kind");
                var resolution = RequireExactToken(
                    resource.Resolution,
                    ["Fixed", "CurrentMaterialSlot", "AvailableSlotInGroup"],
                    "Operation resource resolution");
                if (!string.Equals(kind, "Slot", StringComparison.Ordinal)
                    && !string.Equals(resolution, "Fixed", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Production operation {operationId} resource kind {kind} only supports Fixed resolution.",
                        nameof(resources));
                }

                return new ProjectReleaseOperationResource(
                    RequireProductionValue(resource.BindingId, nameof(resource.BindingId)),
                    kind,
                    RequireProductionValue(
                        resource.TopologyTargetId,
                        nameof(resource.TopologyTargetId)),
                    resolution,
                    NormalizeIdentifiers(
                        resource.EligibleSlotIds,
                        nameof(resource.EligibleSlotIds)));
            })
            .OrderBy(resource => resource.Kind, StringComparer.Ordinal)
            .ThenBy(resource => resource.TopologyTargetId, StringComparer.Ordinal)
            .ThenBy(resource => resource.Resolution, StringComparer.Ordinal)
            .ThenBy(resource => resource.BindingId, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                $"Production operation {operationId} requires resources.",
                nameof(resources));
        }

        EnsureProductionIdentifiersAreUnique(
            normalized.Select(resource => resource.BindingId),
            $"Production operation {operationId} resource BindingIds");
        if (normalized.GroupBy(resource => (
                    resource.Kind,
                    resource.TopologyTargetId,
                    resource.Resolution))
                .Any(group => group.Count() != 1))
        {
            throw new ArgumentException(
                $"Production operation {operationId} resource bindings must be semantically unique.",
                nameof(resources));
        }

        if (normalized.Any(resource =>
                string.Equals(
                    resource.Resolution,
                    "AvailableSlotInGroup",
                    StringComparison.Ordinal) != (resource.EligibleSlotIds.Count > 0)))
        {
            throw new ArgumentException(
                $"Production operation {operationId} AvailableSlotInGroup resources must freeze eligible Slots; other resources cannot declare them.",
                nameof(resources));
        }

        var stationResources = normalized.Where(resource => string.Equals(
            resource.Kind,
            "Station",
            StringComparison.Ordinal)).ToArray();
        if (stationResources.Length != 1
            || !string.Equals(stationResources[0].Resolution, "Fixed", StringComparison.Ordinal)
            || !string.Equals(
                stationResources[0].TopologyTargetId,
                stationSystemId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Production operation {operationId} must freeze exactly one Fixed Station resource equal to {stationSystemId}.",
                nameof(resources));
        }

        return normalized;
    }

    private static ProjectReleaseAuthorizedAction[] NormalizeAuthorizedActions(
        IReadOnlyCollection<ProjectReleaseAuthorizedAction>? actions,
        string operationId)
    {
        var normalized = (actions
                ?? throw new ArgumentException(
                    $"Production operation {operationId} authorized actions are required.",
                    nameof(actions)))
            .Select(action =>
            {
                if (action is null)
                {
                    throw new ArgumentException(
                        $"Production operation {operationId} authorized actions cannot contain null.",
                        nameof(actions));
                }

                return new ProjectReleaseAuthorizedAction(
                    RequireProductionValue(action.ActionId, nameof(action.ActionId)),
                    RequireProductionValue(action.NodeId, nameof(action.NodeId)),
                    RequireExactToken(
                        action.Kind,
                        ["DeviceCommand", "PythonScript"],
                        "authorized action kind"),
                    RequireProductionValue(
                        action.RequiredCapability,
                        nameof(action.RequiredCapability)),
                    RequireProductionValue(action.CommandName, nameof(action.CommandName)),
                    RequireExactToken(
                        action.TargetKind,
                        ["System", "SlotGroup", "Slot", "ProductionUnit", "Capability", "Driver"],
                        "authorized action target kind"),
                    RequireProductionValue(action.TargetId, nameof(action.TargetId)),
                    action.TimeoutMilliseconds > 0
                        ? action.TimeoutMilliseconds
                        : throw new ArgumentException(
                            "Authorized action timeout must be positive.",
                            nameof(actions)),
                    action.LineControllerAuthorizationId is null
                        ? null
                        : RequireProductionValue(
                            action.LineControllerAuthorizationId,
                            nameof(action.LineControllerAuthorizationId)));
            })
            .OrderBy(action => action.ActionId, StringComparer.Ordinal)
            .ToArray();
        EnsureProductionIdentifiersAreUnique(
            normalized.Select(action => action.ActionId),
            $"Production operation {operationId} authorized action ids");
        return normalized;
    }

    private static ProjectReleaseLineControllerAuthorization[] NormalizeLineControllerAuthorizations(
        IReadOnlyCollection<ProjectReleaseLineControllerAuthorization>? authorizations,
        IReadOnlyCollection<ProjectReleaseOperation> operations)
    {
        var normalized = (authorizations
                ?? throw new ArgumentException(
                    "ProductionLine.LineControllerAuthorizations is required.",
                    nameof(authorizations)))
            .Select(authorization => authorization is null
                ? throw new ArgumentException(
                    "ProductionLine.LineControllerAuthorizations cannot contain null.",
                    nameof(authorizations))
                : new ProjectReleaseLineControllerAuthorization(
                    RequireProductionValue(
                        authorization.AuthorizationId,
                        nameof(authorization.AuthorizationId)),
                    RequireProductionValue(
                        authorization.OperationId,
                        nameof(authorization.OperationId)),
                    RequireProductionValue(authorization.ActionId, nameof(authorization.ActionId)),
                    RequireProductionValue(
                        authorization.ControllerSystemId,
                        nameof(authorization.ControllerSystemId)),
                    RequireProductionValue(
                        authorization.ControllerBindingId,
                        nameof(authorization.ControllerBindingId)),
                    RequireProductionValue(
                        authorization.ControllerCapabilityId,
                        nameof(authorization.ControllerCapabilityId)),
                    RequireProductionValue(
                        authorization.ControllerAction,
                        nameof(authorization.ControllerAction)),
                    RequireProductionValue(
                        authorization.TargetStationSystemId,
                        nameof(authorization.TargetStationSystemId)),
                    RequireProductionValue(
                        authorization.TargetSystemId,
                        nameof(authorization.TargetSystemId)),
                    RequireProductionValue(
                        authorization.TargetBindingId,
                        nameof(authorization.TargetBindingId)),
                    RequireProductionValue(
                        authorization.TargetCapabilityId,
                        nameof(authorization.TargetCapabilityId)),
                    RequireProductionValue(
                        authorization.TargetAction,
                        nameof(authorization.TargetAction))))
            .OrderBy(authorization => authorization.AuthorizationId, StringComparer.Ordinal)
            .ToArray();
        EnsureProductionIdentifiersAreUnique(
            normalized.Select(authorization => authorization.AuthorizationId),
            "Line Controller authorization ids");
        if (normalized.GroupBy(authorization => (
                authorization.OperationId,
                authorization.ActionId)).Any(group => group.Count() != 1))
        {
            throw new ArgumentException(
                "Each frozen Operation action can have at most one Line Controller authorization.",
                nameof(authorizations));
        }

        foreach (var authorization in normalized)
        {
            var operationMatches = operations.Where(operation => string.Equals(
                    operation.OperationId,
                    authorization.OperationId,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (operationMatches.Length != 1)
            {
                throw new ArgumentException(
                    $"Line Controller authorization {authorization.AuthorizationId} references an absent Operation.",
                    nameof(authorizations));
            }

            var operation = operationMatches[0];
            var actionMatches = operation.AuthorizedActions.Where(action =>
                    string.Equals(action.ActionId, authorization.ActionId, StringComparison.Ordinal)
                    && string.Equals(
                        action.LineControllerAuthorizationId,
                        authorization.AuthorizationId,
                        StringComparison.Ordinal)
                    && string.Equals(action.Kind, "DeviceCommand", StringComparison.Ordinal)
                    && string.Equals(
                        action.RequiredCapability,
                        authorization.ControllerCapabilityId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        action.CommandName,
                        authorization.ControllerAction,
                        StringComparison.Ordinal)
                    && string.Equals(action.TargetKind, "Driver", StringComparison.Ordinal)
                    && string.Equals(
                        action.TargetId,
                        authorization.TargetBindingId,
                        StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (actionMatches.Length != 1
                || !operation.Resources.Any(resource =>
                    string.Equals(resource.Kind, "Device", StringComparison.Ordinal)
                    && string.Equals(resource.Resolution, "Fixed", StringComparison.Ordinal)
                    && string.Equals(
                        resource.TopologyTargetId,
                        authorization.ControllerBindingId,
                        StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Line Controller authorization {authorization.AuthorizationId} does not match one exact frozen Flow action and controller resource.",
                    nameof(authorizations));
            }
        }

        if (operations.SelectMany(operation => operation.AuthorizedActions)
            .Any(action => action.LineControllerAuthorizationId is not null
                && normalized.Count(authorization => string.Equals(
                    authorization.AuthorizationId,
                    action.LineControllerAuthorizationId,
                    StringComparison.Ordinal)) != 1))
        {
            throw new ArgumentException(
                "Every authorized action Line Controller reference must resolve exactly once.",
                nameof(authorizations));
        }

        return normalized;
    }

    private static string RequireExactToken(
        string value,
        IReadOnlyCollection<string> supported,
        string description) =>
        supported.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new ArgumentException($"Unsupported {description} '{value}'.", nameof(value));

    private static ProjectReleaseRouteTransition NormalizeRouteTransition(
        ProjectReleaseRouteTransition? transition,
        HashSet<string> operationIds)
    {
        if (transition is null)
        {
            throw new ArgumentException(
                "ProductionLine.Transitions cannot contain null.",
                nameof(transition));
        }

        var transitionId = RequireProductionValue(
            transition.TransitionId,
            nameof(transition.TransitionId));
        var sourceOperationId = RequireProductionValue(
            transition.SourceOperationId,
            nameof(transition.SourceOperationId));
        var hasTargetOperation = !string.IsNullOrWhiteSpace(transition.TargetOperationId);
        var hasTerminalDisposition = !string.IsNullOrWhiteSpace(transition.TerminalDisposition);
        if (hasTargetOperation == hasTerminalDisposition)
        {
            throw new ArgumentException(
                $"Production Route Transition {transitionId} requires exactly one target Operation or terminal disposition.",
                nameof(transition));
        }

        var targetOperationId = hasTargetOperation
            ? RequireProductionValue(transition.TargetOperationId!, nameof(transition.TargetOperationId))
            : null;
        var terminalDisposition = hasTerminalDisposition
            ? RequireExactToken(
                transition.TerminalDisposition!,
                ["Completed", "Nonconforming", "Held", "Scrapped"],
                "terminal disposition")
            : null;
        if (!operationIds.Contains(sourceOperationId)
            || targetOperationId is not null && !operationIds.Contains(targetOperationId))
        {
            throw new ArgumentException(
                $"Production Route Transition {transitionId} must reference existing Operations.",
                nameof(transition));
        }

        if (targetOperationId is not null
            && string.Equals(sourceOperationId, targetOperationId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Production Route Transition {transitionId} cannot target its source Operation.",
                nameof(transition));
        }

        var kind = RequireProductionValue(transition.Kind, nameof(transition.Kind));
        if (kind is not ("Sequence" or "Judgement" or "Condition" or "Rework" or "ParallelFork" or "ParallelJoin"))
        {
            throw new ArgumentException(
                $"Production Route Transition {transitionId} kind '{kind}' is invalid.",
                nameof(transition));
        }

        if (terminalDisposition is not null
            && kind is "Rework" or "ParallelFork" or "ParallelJoin")
        {
            throw new ArgumentException(
                $"Production Route Transition {transitionId} kind '{kind}' must target an Operation.",
                nameof(transition));
        }

        var isConditional = kind is "Judgement" or "Rework";
        var judgement = transition.RequiredJudgement is null
            ? null
            : RequireProductionValue(
                transition.RequiredJudgement,
                nameof(transition.RequiredJudgement));
        if (isConditional != (judgement is not null)
            || judgement is not null
            && judgement is not ("Passed" or "Failed" or "Aborted" or "Unknown" or "NotApplicable"))
        {
            throw new ArgumentException(
                $"Production Route Transition {transitionId} has an invalid result judgement.",
                nameof(transition));
        }

        if ((kind == "Rework") != (transition.MaxTraversals is not null)
            || transition.MaxTraversals is <= 0)
        {
            throw new ArgumentException(
                $"Production Route Transition {transitionId} has an invalid rework traversal limit.",
                nameof(transition));
        }

        var isParallel = kind is "ParallelFork" or "ParallelJoin";
        var parallelGroupId = transition.ParallelGroupId is null
            ? null
            : NormalizeParallelGroupId(transition.ParallelGroupId);
        if (isParallel != (parallelGroupId is not null))
        {
            throw new ArgumentException(
                $"Production Route Transition {transitionId} has an invalid parallel group.",
                nameof(transition));
        }

        var hasAllOutputConditionFields = transition.OutputKey is not null
            && transition.ExpectedOutputKind is not null
            && transition.ExpectedOutputValue is not null;
        var hasAnyOutputConditionField = transition.OutputKey is not null
            || transition.ExpectedOutputKind is not null
            || transition.ExpectedOutputValue is not null;
        if (hasAnyOutputConditionField != hasAllOutputConditionFields
            || (kind == "Condition") != hasAllOutputConditionFields)
        {
            throw new ArgumentException(
                $"Production Route Transition {transitionId} has an invalid typed output condition.",
                nameof(transition));
        }

        string? outputKey = null;
        string? expectedOutputKind = null;
        string? expectedOutputValue = null;
        if (hasAllOutputConditionFields)
        {
            outputKey = RequireProductionValue(transition.OutputKey!, nameof(transition.OutputKey));
            expectedOutputKind = RequireProductionValue(
                transition.ExpectedOutputKind!,
                nameof(transition.ExpectedOutputKind));
            if (!Enum.TryParse<ProductionContextValueKind>(
                    expectedOutputKind,
                    ignoreCase: false,
                    out var valueKind)
                || !Enum.IsDefined(valueKind)
                || !string.Equals(valueKind.ToString(), expectedOutputKind, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Production Route Transition {transitionId} output kind '{expectedOutputKind}' is invalid.",
                    nameof(transition));
            }

            expectedOutputValue = RequireProductionValue(
                transition.ExpectedOutputValue!,
                nameof(transition.ExpectedOutputValue));
            _ = new ProductionContextValue(valueKind, expectedOutputValue);
        }

        return new ProjectReleaseRouteTransition(
            transitionId,
            sourceOperationId,
            targetOperationId,
            terminalDisposition,
            kind,
            judgement,
            transition.MaxTraversals,
            parallelGroupId,
            outputKey,
            expectedOutputKind,
            expectedOutputValue);
    }

    private static void ValidateRouteGraph(
        string entryOperationId,
        ProjectReleaseOperation[] operations,
        ProjectReleaseRouteTransition[] transitions)
    {
        var duplicateEdge = transitions
            .GroupBy(transition => (
                transition.SourceOperationId,
                transition.TargetOperationId,
                transition.TerminalDisposition,
                transition.Kind))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateEdge is not null)
        {
            throw new ArgumentException(
                "Production Route cannot contain duplicate semantic Transitions.",
                nameof(transitions));
        }

        var forward = transitions
            .Where(transition => !string.Equals(
                transition.Kind,
                "Rework",
                StringComparison.Ordinal))
            .ToArray();
        var forwardOperations = forward
            .Where(transition => transition.TargetOperationId is not null)
            .ToArray();
        if (forwardOperations.Any(transition => string.Equals(
                transition.TargetOperationId,
                entryOperationId,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Production entry Operation cannot have an incoming forward Transition.",
                nameof(transitions));
        }

        foreach (var operation in operations)
        {
            var outgoing = transitions.Where(transition => string.Equals(
                    transition.SourceOperationId,
                    operation.OperationId,
                    StringComparison.Ordinal))
                .ToArray();
            if (outgoing.Length == 0)
            {
                throw new ArgumentException(
                    $"Production Operation {operation.OperationId} requires an explicit terminal disposition or target Operation.",
                    nameof(transitions));
            }

            if (outgoing.Length == 1 && outgoing[0].Kind is "Sequence" or "ParallelJoin")
            {
                continue;
            }

            var judgementRoutes = outgoing
                .Where(transition => transition.Kind is "Judgement" or "Rework")
                .ToArray();
            var sequenceFallbacks = outgoing
                .Where(transition => transition.Kind == "Sequence")
                .ToArray();
            if (judgementRoutes.Length > 0
                && judgementRoutes.Length + sequenceFallbacks.Length == outgoing.Length)
            {
                var judgementFallbacks = judgementRoutes
                    .Where(transition => transition.Kind == "Judgement")
                    .Select(transition => transition.RequiredJudgement!)
                    .ToHashSet(StringComparer.Ordinal);
                var deterministic = sequenceFallbacks.Length <= 1
                    && judgementRoutes.Where(transition => transition.Kind == "Judgement")
                        .GroupBy(transition => transition.RequiredJudgement, StringComparer.Ordinal)
                        .All(group => group.Count() == 1)
                    && judgementRoutes.Where(transition => transition.Kind == "Rework")
                        .GroupBy(transition => transition.RequiredJudgement, StringComparer.Ordinal)
                        .All(group => group.Count() == 1)
                    && judgementRoutes.Where(transition => transition.Kind == "Rework")
                        .All(transition => judgementFallbacks.Contains(transition.RequiredJudgement!));
                var exhaustive = sequenceFallbacks.Length == 1
                    || SupportedRouteJudgements
                        .All(judgementFallbacks.Contains);
                if (deterministic && exhaustive)
                {
                    continue;
                }
            }

            var conditions = outgoing.Where(transition => transition.Kind == "Condition").ToArray();
            if (conditions.Length > 0
                && conditions.Length + sequenceFallbacks.Length == outgoing.Length)
            {
                var keys = conditions.Select(transition => transition.OutputKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var values = conditions.Select(transition => (
                        transition.ExpectedOutputKind,
                        transition.ExpectedOutputValue))
                    .ToArray();
                if (keys.Length == 1
                    && values.Distinct().Count() == values.Length
                    && sequenceFallbacks.Length == 1)
                {
                    continue;
                }
            }

            if (outgoing.Length >= 2
                     && outgoing.All(transition => transition.Kind == "ParallelFork")
                     && outgoing.Select(transition => transition.ParallelGroupId)
                         .Distinct(StringComparer.Ordinal).Count() == 1)
            {
                continue;
            }

            throw new ArgumentException(
                $"Production Operation {operation.OperationId} has an invalid outgoing Route shape.",
                nameof(transitions));
        }

        var reachable = TraverseFrom(entryOperationId, transitions);
        var unreachable = operations.FirstOrDefault(operation => !reachable.Contains(operation.OperationId));
        if (unreachable is not null)
        {
            throw new ArgumentException(
                $"Production Operation {unreachable.OperationId} is not reachable from the entry Operation.",
                nameof(transitions));
        }

        var indegrees = operations.ToDictionary(operation => operation.OperationId, _ => 0, StringComparer.Ordinal);
        foreach (var transition in forwardOperations)
        {
            indegrees[transition.TargetOperationId!]++;
        }

        var pending = new Queue<string>(
            indegrees.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visitedCount = 0;
        while (pending.TryDequeue(out var operationId))
        {
            visitedCount++;
            foreach (var transition in forwardOperations.Where(candidate => string.Equals(
                         candidate.SourceOperationId,
                         operationId,
                         StringComparison.Ordinal)))
            {
                indegrees[transition.TargetOperationId!]--;
                if (indegrees[transition.TargetOperationId!] == 0)
                {
                    pending.Enqueue(transition.TargetOperationId!);
                }
            }
        }

        if (visitedCount != operations.Length)
        {
            throw new ArgumentException(
                "Production forward Route must be acyclic; loops require bounded Rework Transitions.",
                nameof(transitions));
        }

        var terminals = forward
            .Where(transition => transition.TerminalDisposition is not null)
            .Select(transition => transition.SourceOperationId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var completable = new HashSet<string>(terminals, StringComparer.Ordinal);
        pending = new Queue<string>(terminals);
        while (pending.TryDequeue(out var targetId))
        {
            foreach (var transition in forward.Where(candidate => string.Equals(
                         candidate.TargetOperationId,
                         targetId,
                         StringComparison.Ordinal)))
            {
                if (completable.Add(transition.SourceOperationId))
                {
                    pending.Enqueue(transition.SourceOperationId);
                }
            }
        }

        var trapped = operations.FirstOrDefault(operation => !completable.Contains(operation.OperationId));
        if (trapped is not null)
        {
            throw new ArgumentException(
                $"Production Operation {trapped.OperationId} has no forward path to a terminal disposition.",
                nameof(transitions));
        }

        foreach (var rework in transitions.Where(transition => transition.Kind == "Rework"))
        {
            if (!TraverseFrom(rework.TargetOperationId!, forwardOperations).Contains(rework.SourceOperationId))
            {
                throw new ArgumentException(
                    $"Rework Transition {rework.TransitionId} must return to an earlier Operation.",
                    nameof(transitions));
            }
        }

        ValidateParallelGroups(forwardOperations, transitions);
    }

    private static void ValidateParallelGroups(
        IReadOnlyCollection<ProjectReleaseRouteTransition> forwardTransitions,
        IReadOnlyCollection<ProjectReleaseRouteTransition> transitions)
    {
        var groups = transitions
            .Where(transition => transition.ParallelGroupId is not null)
            .GroupBy(transition => transition.ParallelGroupId!, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in groups)
        {
            var forks = group.Where(transition => transition.Kind == "ParallelFork").ToArray();
            var joins = group.Where(transition => transition.Kind == "ParallelJoin").ToArray();
            if (forks.Length < 2
                || joins.Length < 2
                || forks.Length != joins.Length
                || forks.Select(transition => transition.SourceOperationId)
                    .Distinct(StringComparer.Ordinal).Count() != 1
                || joins.Select(transition => transition.TargetOperationId)
                    .Distinct(StringComparer.Ordinal).Count() != 1
                || forks.Select(transition => transition.TargetOperationId)
                    .Distinct(StringComparer.Ordinal).Count() != forks.Length
                || joins.Select(transition => transition.SourceOperationId)
                    .Distinct(StringComparer.Ordinal).Count() != joins.Length)
            {
                throw new ArgumentException(
                    $"Parallel group {group.Key} must define equal distinct fork and join branches.",
                    nameof(transitions));
            }

            var forkSource = forks[0].SourceOperationId;
            var joinTarget = joins[0].TargetOperationId!;
            if (string.Equals(forkSource, joinTarget, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Parallel group {group.Key} fork and join Operations must be distinct.",
                    nameof(transitions));
            }

            var joinSources = joins.Select(transition => transition.SourceOperationId)
                .ToHashSet(StringComparer.Ordinal);
            var assignedJoinSources = new HashSet<string>(StringComparer.Ordinal);
            var branchSets = new List<HashSet<string>>();
            foreach (var fork in forks)
            {
                var branch = TraverseUntil(
                    fork.TargetOperationId!,
                    joinTarget,
                    forwardTransitions);
                var branchJoinSources = branch.Where(joinSources.Contains).ToArray();
                if (branchJoinSources.Length != 1 || !assignedJoinSources.Add(branchJoinSources[0]))
                {
                    throw new ArgumentException(
                        $"Parallel group {group.Key} must map each fork to one distinct join branch.",
                        nameof(transitions));
                }

                if (branch.Any(operationId => forwardTransitions.Any(transition =>
                        string.Equals(
                            transition.SourceOperationId,
                            operationId,
                            StringComparison.Ordinal)
                        && transition.Kind is "ParallelFork" or "ParallelJoin"
                        && !string.Equals(
                            transition.ParallelGroupId,
                            group.Key,
                            StringComparison.Ordinal))))
                {
                    throw new ArgumentException(
                        $"Parallel group {group.Key} cannot contain a nested parallel group.",
                        nameof(transitions));
                }

                var branchTerminal = branch.FirstOrDefault(operationId =>
                    transitions.Any(transition => string.Equals(
                            transition.SourceOperationId,
                            operationId,
                            StringComparison.Ordinal)
                        && transition.TerminalDisposition is not null));
                if (branchTerminal is not null)
                {
                    throw new ArgumentException(
                        $"Parallel group {group.Key} branch {fork.TargetOperationId} can reach a terminal disposition before its join.",
                        nameof(transitions));
                }

                if (branchSets.Any(existing => existing.Overlaps(branch)))
                {
                    throw new ArgumentException(
                        $"Parallel group {group.Key} branches must remain disjoint until the join.",
                        nameof(transitions));
                }

                branchSets.Add(branch);
            }

            if (!assignedJoinSources.SetEquals(joinSources))
            {
                throw new ArgumentException(
                    $"Parallel group {group.Key} has an unmatched join branch.",
                    nameof(transitions));
            }

            foreach (var branch in branchSets)
            {
                foreach (var operationId in branch)
                {
                    var invalidIncoming = forwardTransitions.Any(transition =>
                        string.Equals(
                            transition.TargetOperationId,
                            operationId,
                            StringComparison.Ordinal)
                        && !branch.Contains(transition.SourceOperationId)
                        && !(string.Equals(
                                transition.SourceOperationId,
                                forkSource,
                                StringComparison.Ordinal)
                            && transition.Kind == "ParallelFork"
                            && string.Equals(
                                transition.ParallelGroupId,
                                group.Key,
                                StringComparison.Ordinal)));
                    if (invalidIncoming)
                    {
                        throw new ArgumentException(
                            $"Parallel group {group.Key} branch cannot be entered outside its fork.",
                            nameof(transitions));
                    }

                    var invalidJoin = forwardTransitions.Any(transition =>
                        string.Equals(
                            transition.SourceOperationId,
                            operationId,
                            StringComparison.Ordinal)
                        && string.Equals(
                            transition.TargetOperationId,
                            joinTarget,
                            StringComparison.Ordinal)
                        && (transition.Kind != "ParallelJoin"
                            || !string.Equals(
                                transition.ParallelGroupId,
                                group.Key,
                                StringComparison.Ordinal)));
                    if (invalidJoin)
                    {
                        throw new ArgumentException(
                            $"Parallel group {group.Key} branches must enter their join through ParallelJoin Transitions.",
                            nameof(transitions));
                    }
                }
            }
        }
    }

    private static HashSet<string> TraverseFrom(
        string start,
        IEnumerable<ProjectReleaseRouteTransition> transitions)
    {
        var edges = transitions.ToLookup(
            transition => transition.SourceOperationId,
            StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var operationId))
        {
            if (!visited.Add(operationId))
            {
                continue;
            }

            foreach (var transition in edges[operationId])
            {
                if (transition.TargetOperationId is not null)
                {
                    pending.Enqueue(transition.TargetOperationId);
                }
            }
        }

        return visited;
    }

    private static HashSet<string> TraverseUntil(
        string start,
        string stop,
        IEnumerable<ProjectReleaseRouteTransition> transitions)
    {
        var edges = transitions.ToLookup(
            transition => transition.SourceOperationId,
            StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var operationId))
        {
            if (string.Equals(operationId, stop, StringComparison.Ordinal)
                || !visited.Add(operationId))
            {
                continue;
            }

            foreach (var transition in edges[operationId])
            {
                if (transition.TargetOperationId is not null)
                {
                    pending.Enqueue(transition.TargetOperationId);
                }
            }
        }

        return visited;
    }

    private static string NormalizeParallelGroupId(string value)
    {
        var normalized = RequireProductionValue(value, nameof(value));
        if (normalized.Any(char.IsControl)
            || normalized.Contains('/', StringComparison.Ordinal)
            || normalized.Contains('\\', StringComparison.Ordinal)
            || normalized is "." or "..")
        {
            throw new ArgumentException(
                "Production parallel group id must be a portable path segment.",
                nameof(value));
        }

        return normalized;
    }

    private static ProjectReleaseExternalProgramResource[] NormalizeExternalPrograms(
        IReadOnlyCollection<ProjectReleaseExternalProgramResource>? resources)
    {
        if (resources is null)
        {
            throw new ArgumentException("ExternalProgramResources collection is required.", nameof(resources));
        }

        var normalized = resources.Select(resource =>
        {
            if (resource is null)
            {
                throw new ArgumentException("ExternalProgramResources cannot contain null.", nameof(resources));
            }

            var resourceId = ExternalProgramResourceContract.PortableId(
                resource.ResourceId,
                nameof(resource.ResourceId));
            var relativePath = ExternalProgramResourceContract.CanonicalRelativePath(
                resource.ResourceRelativePath,
                nameof(resource.ResourceRelativePath));
            var expectedPath = $"{ExternalProgramResourceContract.ResourceDirectoryName}/{resourceId}";
            if (!string.Equals(relativePath, expectedPath, StringComparison.Ordinal)
                || !Enum.TryParse<ExternalProgramLaunchKind>(
                    resource.LaunchKind,
                    ignoreCase: false,
                    out var launchKind)
                || !Enum.IsDefined(launchKind)
                || !string.Equals(resource.LaunchKind, launchKind.ToString(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"External program resource {resourceId} identity or launch kind is invalid.",
                    nameof(resources));
            }

            var files = (resource.Files
                    ?? throw new ArgumentException("External program Files is required.", nameof(resources)))
                .Select(file =>
                {
                    if (file is null)
                    {
                        throw new ArgumentException("External program Files cannot contain null.", nameof(resources));
                    }

                    var path = ExternalProgramResourceContract.CanonicalRelativePath(
                        file.RelativePath,
                        nameof(file.RelativePath),
                        ExternalProgramResourceContract.ResourceDirectoryName);
                    if (!path.StartsWith(relativePath + "/files/", StringComparison.Ordinal)
                        || file.SizeBytes < 0
                        || !ExternalProgramResourceContract.IsSha256(file.Sha256))
                    {
                        throw new ArgumentException(
                            $"External program resource {resourceId} file inventory is invalid.",
                            nameof(resources));
                    }

                    return new ProjectReleaseExternalProgramFile(path, file.SizeBytes, file.Sha256);
                })
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray();
            EnsureProductionIdentifiersAreUnique(
                files.Select(file => file.RelativePath),
                $"External program resource {resourceId} file paths");

            var inputMappings = resource.InputMappings.Select(mapping =>
                    new ExternalProgramInputMapping(mapping.Source, mapping.Target))
                .ToArray();
            var resultMappings = resource.ResultMappings.Select(mapping =>
                    new ExternalProgramResultMapping(
                        mapping.SourcePath,
                        mapping.TargetKey,
                        ParseExact<ProductionContextValueKind>(mapping.ValueKind, "external program result kind")))
                .ToArray();
            var localResource = new ExternalProgramResource(
                resourceId,
                resource.DisplayName,
                resource.CapabilityId,
                resource.CommandName,
                launchKind,
                resource.EntryPoint,
                resource.ResourceProviderKind,
                resource.ProviderKey,
                resource.ArgumentTemplates,
                inputMappings,
                resultMappings,
                new ExternalProgramOutcomeMapping(
                    resource.OutcomeMapping.SourcePath,
                    resource.OutcomeMapping.PassedToken,
                    resource.OutcomeMapping.FailedToken,
                    resource.OutcomeMapping.AbortedToken),
                new ExternalProgramPermissionProfile(
                    resource.PermissionProfile.ProfileName,
                    resource.PermissionProfile.NetworkAccessAllowed,
                    resource.PermissionProfile.AllowedEnvironmentVariables),
                new ExternalProgramExecutionLimits(
                    resource.ExecutionLimits.TimeoutMilliseconds,
                    resource.ExecutionLimits.MaximumProcessCount,
                    resource.ExecutionLimits.MaximumWorkingSetBytes,
                    resource.ExecutionLimits.MaximumCpuTimeMilliseconds,
                    resource.ExecutionLimits.MaximumStandardOutputBytes,
                    resource.ExecutionLimits.MaximumStandardErrorBytes,
                    resource.ExecutionLimits.MaximumArtifactCount,
                    resource.ExecutionLimits.MaximumArtifactBytes,
                    resource.ExecutionLimits.MaximumTotalArtifactBytes),
                files.Select(file => new ExternalProgramResourceFile(
                    file.RelativePath[(relativePath.Length + 1)..],
                    file.SizeBytes,
                    file.Sha256)).ToArray(),
                resource.ContentSha256,
                DateTimeOffset.UnixEpoch);
            ExternalProgramResourceValidator.ValidateFrozenResource(localResource);

            return new ProjectReleaseExternalProgramResource(
                resourceId,
                localResource.DisplayName,
                localResource.CapabilityId,
                localResource.CommandName,
                launchKind.ToString(),
                localResource.EntryPoint,
                localResource.ProviderKind,
                localResource.ProviderKey,
                localResource.ArgumentTemplates.ToArray(),
                localResource.InputMappings.Select(mapping => new ProjectReleaseExternalProgramInputMapping(
                    mapping.Source,
                    mapping.Target)).ToArray(),
                localResource.ResultMappings.Select(mapping => new ProjectReleaseExternalProgramResultMapping(
                    mapping.SourcePath,
                    mapping.TargetKey,
                    mapping.ValueKind.ToString())).ToArray(),
                new ProjectReleaseExternalProgramOutcomeMapping(
                    localResource.OutcomeMapping.SourcePath,
                    localResource.OutcomeMapping.PassedToken,
                    localResource.OutcomeMapping.FailedToken,
                    localResource.OutcomeMapping.AbortedToken),
                resource.PermissionProfile with
                {
                    AllowedEnvironmentVariables = resource.PermissionProfile.AllowedEnvironmentVariables
                        .Order(StringComparer.Ordinal)
                        .ToArray()
                },
                resource.ExecutionLimits,
                files,
                localResource.ContentSha256,
                relativePath);
        }).OrderBy(resource => resource.ResourceId, StringComparer.Ordinal).ToArray();

        EnsureProductionIdentifiersAreUnique(
            normalized.Select(resource => resource.ResourceId),
            "External program resource ids");
        return normalized;
    }

    private static T ParseExact<T>(string value, string description)
        where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed)
            || !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported {description} '{value}'.", nameof(value));
        }

        return parsed;
    }

    private static (string SchemaVersion, string Sha256, string CanonicalJson) NormalizeFrozenFlowIr(
        string schemaVersion,
        string sha256,
        string canonicalJson,
        string description)
    {
        var normalizedSchemaVersion = RequireProductionValue(schemaVersion, $"{description} schema version");
        var normalizedCanonicalJson = RequireProductionValue(canonicalJson, $"{description} canonical JSON");
        ValidateSha256(sha256, $"{description} SHA-256", argumentError: true);
        try
        {
            using var _ = JsonDocument.Parse(normalizedCanonicalJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"{description} contains invalid canonical JSON: {exception.Message}",
                nameof(canonicalJson),
                exception);
        }

        var computedSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCanonicalJson)))
            .ToLowerInvariant();
        if (!string.Equals(sha256, computedSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} SHA-256 does not match its canonical JSON.",
                nameof(sha256));
        }

        return (normalizedSchemaVersion, sha256, normalizedCanonicalJson);
    }

    private static string RequireProductionValue(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException($"{fieldName} must be a non-empty canonical value.", fieldName);
        }

        return value;
    }

    private static void EnsureProductionIdentifiersAreUnique(
        IEnumerable<string> values,
        string description)
    {
        var identifiers = values.ToArray();
        if (identifiers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != identifiers.Length)
        {
            throw new ArgumentException(
                $"{description} must be unique and cannot differ only by case.",
                nameof(values));
        }
    }

    private static ProjectReleasePackageDependencyLock[] NormalizePackageDependencies(
        IReadOnlyCollection<ProjectReleasePackageDependencyLock>? dependencies)
    {
        if (dependencies is null)
        {
            throw new ArgumentException("PackageDependencies collection is required.", nameof(dependencies));
        }

        var normalized = dependencies.Select(dependency =>
        {
            ArgumentNullException.ThrowIfNull(dependency);
            var packageContentSha256 = NormalizeSha256(
                dependency.PackageContentSha256,
                nameof(dependency.PackageContentSha256));
            var manifestSha256 = NormalizeSha256(
                dependency.ManifestSha256,
                nameof(dependency.ManifestSha256));
            var entryAssemblySha256 = NormalizeSha256(
                dependency.EntryAssemblySha256,
                nameof(dependency.EntryAssemblySha256));
            var packageRelativePath = RequireValue(
                dependency.PackageRelativePath,
                nameof(dependency.PackageRelativePath));
            if (!string.Equals(
                    packageRelativePath,
                    $"packages/{packageContentSha256}",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "PackageRelativePath must be the content-addressed packages/<package SHA-256> path.",
                    nameof(dependencies));
            }

            var commands = (dependency.Commands
                    ?? throw new ArgumentException("Package dependency commands are required.", nameof(dependencies)))
                .Select(command => command is null
                    ? throw new ArgumentException("Package dependency commands cannot contain null.", nameof(dependencies))
                    : new ProjectReleasePackageCommandLock(
                        RequireValue(command.Kind, nameof(command.Kind)),
                        RequireValue(command.CommandDefinitionId, nameof(command.CommandDefinitionId)),
                        RequireValue(command.CapabilityId, nameof(command.CapabilityId)),
                        RequireValue(command.CommandName, nameof(command.CommandName))))
                .Distinct()
                .OrderBy(command => command.Kind, StringComparer.Ordinal)
                .ThenBy(command => command.CommandDefinitionId, StringComparer.Ordinal)
                .ToArray();
            if (commands.Length == 0)
            {
                throw new ArgumentException("Package dependency must lock at least one command.", nameof(dependencies));
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = (dependency.Files
                    ?? throw new ArgumentException("Package dependency files are required.", nameof(dependencies)))
                .Select(file =>
                {
                    if (file is null)
                    {
                        throw new ArgumentException("Package dependency files cannot contain null.", nameof(dependencies));
                    }

                    var relativePath = NormalizePackageFilePath(file.RelativePath);
                    if (!paths.Add(relativePath))
                    {
                        throw new ArgumentException(
                            $"Package file path '{relativePath}' is duplicated.",
                            nameof(dependencies));
                    }

                    if (file.SizeBytes < 0)
                    {
                        throw new ArgumentException(
                            $"Package file '{relativePath}' has a negative size.",
                            nameof(dependencies));
                    }

                    return new ProjectReleasePackageFile(
                        relativePath,
                        file.SizeBytes,
                        NormalizeSha256(file.Sha256, $"package file '{relativePath}' SHA-256"));
                })
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray();
            if (files.Length == 0)
            {
                throw new ArgumentException("Package dependency must contain at least one file.", nameof(dependencies));
            }

            var computedContentSha256 = ComputePackageContentSha256(files);
            if (!string.Equals(computedContentSha256, packageContentSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "PackageContentSha256 does not match the canonical package file inventory.",
                    nameof(dependencies));
            }

            var manifestRelativePath = NormalizePackageFilePath(dependency.ManifestRelativePath);
            var entryAssemblyRelativePath = NormalizePackageFilePath(dependency.EntryAssemblyRelativePath);
            var manifestFile = files.SingleOrDefault(file => string.Equals(
                file.RelativePath,
                manifestRelativePath,
                StringComparison.Ordinal));
            var entryAssemblyFile = files.SingleOrDefault(file => string.Equals(
                file.RelativePath,
                entryAssemblyRelativePath,
                StringComparison.Ordinal));
            if (manifestFile is null || !string.Equals(manifestFile.Sha256, manifestSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "ManifestSha256 does not match the locked manifest file.",
                    nameof(dependencies));
            }

            if (entryAssemblyFile is null
                || !string.Equals(entryAssemblyFile.Sha256, entryAssemblySha256, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "EntryAssemblySha256 does not match the locked entry assembly file.",
                    nameof(dependencies));
            }

            return new ProjectReleasePackageDependencyLock(
                RequireValue(dependency.CapabilityId, nameof(dependency.CapabilityId)),
                RequireValue(dependency.BindingId, nameof(dependency.BindingId)),
                RequireValue(dependency.ProviderKind, nameof(dependency.ProviderKind)),
                RequireValue(dependency.ProviderKey, nameof(dependency.ProviderKey)),
                RequireValue(dependency.OwnerSystemId, nameof(dependency.OwnerSystemId)),
                RequireValue(dependency.OwnerStationSystemId, nameof(dependency.OwnerStationSystemId)),
                RequireValue(dependency.PackageId, nameof(dependency.PackageId)),
                RequireValue(dependency.PluginId, nameof(dependency.PluginId)),
                RequireValue(dependency.PackageVersion, nameof(dependency.PackageVersion)),
                packageContentSha256,
                manifestSha256,
                entryAssemblySha256,
                RequireValue(dependency.ContractVersion, nameof(dependency.ContractVersion)),
                RequireValue(dependency.RuntimeIdentifier, nameof(dependency.RuntimeIdentifier)),
                RequireValue(dependency.AbiVersion, nameof(dependency.AbiVersion)),
                packageRelativePath,
                manifestRelativePath,
                entryAssemblyRelativePath,
                commands,
                files,
                string.IsNullOrWhiteSpace(dependency.SourcePackagePath)
                    ? null
                    : Path.GetFullPath(dependency.SourcePackagePath));
        })
        .OrderBy(dependency => dependency.OwnerSystemId, StringComparer.Ordinal)
        .ThenBy(dependency => dependency.CapabilityId, StringComparer.Ordinal)
        .ThenBy(dependency => dependency.BindingId, StringComparer.Ordinal)
        .ToArray();

        if (normalized
            .GroupBy(dependency => $"{dependency.OwnerStationSystemId}\u001f{dependency.OwnerSystemId}\u001f{dependency.CapabilityId}\u001f{dependency.BindingId}", StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw new ArgumentException(
                "Package dependency capability/binding identities must be unique.",
                nameof(dependencies));
        }

        foreach (var contentGroup in normalized.GroupBy(
                     dependency => dependency.PackageContentSha256,
                     StringComparer.Ordinal))
        {
            var canonicalFiles = contentGroup.First().Files;
            if (contentGroup.Skip(1).Any(item => !item.Files.SequenceEqual(canonicalFiles)))
            {
                throw new ArgumentException(
                    "Dependencies sharing a package content SHA-256 must have the same file inventory.",
                    nameof(dependencies));
            }
        }

        return normalized;
    }

    private static string ComputePackageContentSha256(
        IReadOnlyCollection<ProjectReleasePackageFile> files)
    {
        var canonical = new StringBuilder();
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            canonical.Append(file.RelativePath)
                .Append('\0')
                .Append(file.SizeBytes.ToString(CultureInfo.InvariantCulture))
                .Append('\0')
                .Append(file.Sha256)
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string NormalizePackageFilePath(string value)
    {
        var relativePath = RequireValue(value, "package file relative path");
        if (Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.Any(char.IsControl)
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                $"Package file path '{relativePath}' is not canonical.",
                nameof(value));
        }

        return relativePath;
    }

    private static string NormalizeSha256(string value, string fieldName)
    {
        ValidateSha256(value, fieldName, argumentError: true);
        return value;
    }

    private static string[] NormalizeIdentifiers(
        IReadOnlyCollection<string>? values,
        string fieldName)
    {
        if (values is null)
        {
            throw new ArgumentException($"{fieldName} collection is required.", fieldName);
        }

        var normalized = values
            .Select(value => RequireValue(value, fieldName))
            .ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException(
                $"{fieldName} cannot contain duplicate frozen identities.",
                fieldName);
        }

        return normalized
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string RequireValue(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{fieldName} cannot be empty.", fieldName)
            : value.Trim();
    }

    private static FileTreeSnapshot InspectFileTree(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Directory '{rootPath}' does not exist.");
        }

        RejectReparsePoint(rootPath);
        var directories = new List<string>();
        var files = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(Path.GetFullPath(rootPath));

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         currentDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(entry);
                var relativePath = ProjectReleaseArtifactPath.GetDocumentPath(rootPath, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(relativePath);
                    pendingDirectories.Push(entry);
                }
                else
                {
                    files.Add(relativePath);
                }
            }
        }

        return new FileTreeSnapshot(
            directories.Order(StringComparer.Ordinal).ToArray(),
            files.Order(StringComparer.Ordinal).ToArray());
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Release source path '{path}' is a reparse point and cannot be included.");
        }
    }

    private static async ValueTask WriteManifestAsync(
        string releaseRootPath,
        ProjectReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        var manifestPath = ProjectReleaseArtifactPath.GetManifestPath(releaseRootPath);
        await using var stream = new FileStream(
            manifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer
            .SerializeAsync(stream, manifest, ManifestJsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ProjectReleaseManifest> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            throw InvalidRelease(manifestPath, "is missing");
        }

        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer
                .DeserializeAsync<ProjectReleaseManifest>(stream, ManifestJsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw InvalidRelease(manifestPath, "is empty");
        }
        catch (JsonException exception)
        {
            throw InvalidRelease(manifestPath, "contains invalid JSON", exception);
        }
    }

    private static string ComputeContentSha256(ProjectReleaseManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest with { ContentSha256 = string.Empty },
            CanonicalJsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async ValueTask<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool CanonicalJsonEquals<T>(T first, T second)
    {
        var firstBytes = JsonSerializer.SerializeToUtf8Bytes(first, CanonicalJsonOptions);
        var secondBytes = JsonSerializer.SerializeToUtf8Bytes(second, CanonicalJsonOptions);
        return firstBytes.AsSpan().SequenceEqual(secondBytes);
    }

    private static void ValidateSha256(string? value, string fieldName, bool argumentError)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || !value.All(Uri.IsHexDigit)
            || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            if (argumentError)
            {
                throw new ArgumentException(
                    $"{fieldName} must be a lowercase 64-character SHA-256 value.",
                    fieldName);
            }

            throw new InvalidDataException(
                $"{fieldName} must be a lowercase 64-character SHA-256 value.");
        }
    }

    private static ProjectReleaseArtifactDescriptor ToDescriptor(
        string releaseRootPath,
        ProjectReleaseManifest manifest)
    {
        return new ProjectReleaseArtifactDescriptor(
            manifest.SnapshotId,
            manifest.ProjectId,
            manifest.ApplicationId,
            manifest.PublishedAtUtc,
            manifest.ContentSha256,
            releaseRootPath,
            ProjectReleaseArtifactPath.GetSourceRootPath(releaseRootPath),
            manifest.ApplicationProjectRelativePath,
            ProjectReleaseArtifactPath.GetManifestPath(releaseRootPath),
            manifest.Files);
    }

    private static string FormatPaths(string[] paths)
    {
        return paths.Length == 0 ? "none" : string.Join(", ", paths);
    }

    private static void TryDeleteStagingDirectory(string releasesPath, string stagingRootPath)
    {
        ProjectReleaseArtifactPath.EnsureStagingPath(releasesPath, stagingRootPath);
        if (Directory.Exists(stagingRootPath))
        {
            Directory.Delete(stagingRootPath, recursive: true);
        }
    }

    private static InvalidDataException InvalidRelease(
        string manifestPath,
        string message,
        Exception? innerException = null)
    {
        return new InvalidDataException(
            $"Project release manifest '{manifestPath}' {message}.",
            innerException);
    }

    private sealed record FileTreeSnapshot(string[] Directories, string[] Files);

    private sealed record EmbeddedConfigurationSnapshot(
        string SnapshotId,
        string ProcessDefinitionId,
        string ProcessVersionId,
        string StationProfileId,
        string Status);

    private sealed record EmbeddedStationProfile(
        string StationProfileId,
        string StationSystemId);
}
