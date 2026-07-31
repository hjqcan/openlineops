using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;

public sealed class FileSystemAutomationProjectManifestStore :
    IAutomationProjectManifestStore,
    IProjectApplicationPluginPackageReferenceStore
{
    private static readonly ProjectWorkspaceWriteLockPool ApplicationFileLocks = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public string GetProjectRootPath(string projectTarget)
    {
        if (string.IsNullOrWhiteSpace(projectTarget))
        {
            throw new ArgumentException("Project target cannot be empty.", nameof(projectTarget));
        }

        var fullPath = Path.GetFullPath(projectTarget.Trim());
        RejectNonCanonicalProjectFileExtension(fullPath);
        if (IsProjectFilePath(fullPath))
        {
            var projectRoot = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException($"Project file '{fullPath}' has no parent directory.");
            ProjectWorkspacePathGuard.EnsureOrdinaryDirectoryOrMissing(
                projectRoot,
                "Project root");
            return projectRoot;
        }

        if (File.Exists(fullPath))
        {
            throw new InvalidDataException(
                $"Project target '{fullPath}' must be a directory or a canonical lowercase {AutomationProjectFileConvention.ProjectFileExtension} file.");
        }

        ProjectWorkspacePathGuard.EnsureOrdinaryDirectoryOrMissing(
            fullPath,
            "Project root");
        return fullPath;
    }

    public string GetManifestPath(string projectTarget, string? projectId = null)
    {
        if (string.IsNullOrWhiteSpace(projectTarget))
        {
            throw new ArgumentException("Project target cannot be empty.", nameof(projectTarget));
        }

        var fullTarget = Path.GetFullPath(projectTarget.Trim());
        RejectNonCanonicalProjectFileExtension(fullTarget);
        if (IsProjectFilePath(fullTarget))
        {
            var explicitRoot = Path.GetDirectoryName(fullTarget)
                ?? throw new InvalidDataException($"Project file '{fullTarget}' has no parent directory.");
            ProjectWorkspacePathGuard.EnsureOrdinaryDirectoryOrMissing(
                explicitRoot,
                "Project root");
            ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                fullTarget,
                "Project file");
            var explicitRootFiles = FindProjectFiles(explicitRoot);
            if (explicitRootFiles.Length > 1)
            {
                throw new InvalidDataException(
                    $"Project directory '{explicitRoot}' contains multiple {AutomationProjectFileConvention.ProjectFileExtension} files: {string.Join(", ", explicitRootFiles.Select(Path.GetFileName))}.");
            }

            return fullTarget;
        }

        var projectRoot = GetProjectRootPath(fullTarget);
        ProjectWorkspacePathGuard.EnsureOrdinaryDirectoryOrMissing(
            projectRoot,
            "Project root");
        var projectFiles = FindProjectFiles(projectRoot);
        if (projectFiles.Length > 1)
        {
            throw new InvalidDataException(
                $"Project directory '{projectRoot}' contains multiple {AutomationProjectFileConvention.ProjectFileExtension} files: {string.Join(", ", projectFiles.Select(Path.GetFileName))}.");
        }

        if (projectFiles.Length == 1)
        {
            return projectFiles[0];
        }

        var fileName = string.IsNullOrWhiteSpace(projectId)
            ? $"openlineops{AutomationProjectFileConvention.ProjectFileExtension}"
            : AutomationProjectFileConvention.GetProjectFileName(projectId);
        return Path.Combine(projectRoot, fileName);
    }

    public async ValueTask SaveAsync(
        AutomationProjectManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();

        var projectRoot = GetProjectRootPath(manifest.ProjectPath);
        var normalized = NormalizeForSave(manifest, projectRoot);
        var existingProjectFiles = FindProjectFiles(projectRoot);
        if (existingProjectFiles.Length > 1)
        {
            throw new InvalidDataException(
                $"Project directory '{projectRoot}' contains multiple {AutomationProjectFileConvention.ProjectFileExtension} files.");
        }

        var projectFilePath = existingProjectFiles.SingleOrDefault()
            ?? Path.Combine(
                projectRoot,
                AutomationProjectFileConvention.GetProjectFileName(normalized.ProjectId));
        ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
            projectFilePath,
            "Project file");

        var applicationFilePaths = normalized.Applications.ToDictionary(
            static application => application.ApplicationId,
            application =>
            {
                var applicationFilePath = AutomationProjectFileConvention.ResolveApplicationProjectPath(
                    projectRoot,
                    application.ProjectFilePath);
                ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    applicationFilePath,
                    $"Application {application.ApplicationId} project file");
                return applicationFilePath;
            },
            StringComparer.Ordinal);
        var applicationDirectories = applicationFilePaths.Values
            .SelectMany(applicationFilePath => GetApplicationDirectories(
                Path.GetDirectoryName(applicationFilePath)!))
            .ToArray();
        foreach (var directory in applicationDirectories)
        {
            ProjectWorkspacePathGuard.EnsureOrdinaryDirectoryOrMissing(
                directory.Path,
                directory.Description);
        }

        ProjectWorkspacePathGuard.CreateOrdinaryDirectory(projectRoot, "Project root");
        foreach (var directory in applicationDirectories)
        {
            ProjectWorkspacePathGuard.CreateOrdinaryDirectory(directory.Path, directory.Description);
        }

        foreach (var application in normalized.Applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applicationFilePath = applicationFilePaths[application.ApplicationId];

            using var applicationLease = await ApplicationFileLocks.AcquireAsync(
                    applicationFilePath,
                    cancellationToken)
                .ConfigureAwait(false);
            var pluginPackageReferences = application.PluginPackageReferences;
            if (File.Exists(applicationFilePath))
            {
                var existing = await ReadAsync<AutomationApplicationProjectFile>(
                        applicationFilePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateApplicationFile(
                    new AutomationProjectApplicationReference(
                        application.ApplicationId,
                        application.ProjectFilePath),
                    existing,
                    applicationFilePath);
                pluginPackageReferences = existing.PluginPackageReferences;
            }

            var applicationFile = new AutomationApplicationProjectFile(
                AutomationApplicationProjectFile.CurrentSchemaVersion,
                AutomationApplicationProjectFile.CurrentFormatVersion,
                AutomationApplicationProjectFile.KindName,
                AutomationProjectManifest.ProductName,
                application.ApplicationId,
                application.DisplayName,
                AutomationApplicationProjectFile.CurrentResourceLayoutVersion,
                application.TopologyId,
                application.ProcessDefinitionIds,
                ProjectApplicationPluginPackageReferenceContract.ValidateAndOrder(
                    pluginPackageReferences));

            await WriteAtomicallyAsync(applicationFilePath, applicationFile, cancellationToken)
                .ConfigureAwait(false);
        }

        var projectFile = new AutomationProjectFile(
            AutomationProjectFile.CurrentSchemaVersion,
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectFile.KindName,
            AutomationProjectManifest.ProductName,
            normalized.ProjectId,
            normalized.DisplayName,
            normalized.CreatedAtUtc,
            normalized.UpdatedAtUtc,
            normalized.ActiveSnapshotId,
            normalized.Applications
                .OrderBy(application => application.ApplicationId, StringComparer.Ordinal)
                .Select(application => new AutomationProjectApplicationReference(
                    application.ApplicationId,
                    application.ProjectFilePath))
                .ToArray(),
            normalized.Snapshots
                .OrderBy(snapshot => snapshot.PublishedAtUtc)
                .ThenBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
                .Select(ToProjectFileSnapshot)
                .ToArray());

        // The root project file is the commit marker: every referenced Application
        // project is durable before the root reference is replaced.
        await WriteAtomicallyAsync(projectFilePath, projectFile, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AutomationProjectManifest?> LoadAsync(
        string projectTarget,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var projectRoot = GetProjectRootPath(projectTarget);
        ProjectWorkspacePathGuard.EnsureOrdinaryDirectoryOrMissing(
            projectRoot,
            "Project root");
        var projectFilePath = GetManifestPath(projectTarget);
        if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                projectFilePath,
                "Project file"))
        {
            return null;
        }

        return await LoadCurrentAsync(projectRoot, projectFilePath, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProjectApplicationManifest?> LoadApplicationProjectAsync(
        string projectRootPath,
        string applicationProjectTarget,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projectRoot = GetProjectRootPath(projectRootPath);
        var relativePath = GetApplicationProjectRelativePath(projectRoot, applicationProjectTarget);
        AutomationProjectFileConvention.ValidateApplicationProjectRelativePath(relativePath);
        var applicationFilePath = AutomationProjectFileConvention.ResolveApplicationProjectPath(
            projectRoot,
            relativePath);
        if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                applicationFilePath,
                "Application project file"))
        {
            return null;
        }

        var applicationFile = await ReadAsync<AutomationApplicationProjectFile>(
                applicationFilePath,
                cancellationToken)
            .ConfigureAwait(false);
        var reference = new AutomationProjectApplicationReference(
            applicationFile.ApplicationId,
            relativePath);
        ValidateApplicationFile(reference, applicationFile, applicationFilePath);

        return new ProjectApplicationManifest(
            applicationFile.ApplicationId,
            applicationFile.DisplayName,
            applicationFile.TopologyId,
            NormalizeStrings(applicationFile.ProcessDefinitionIds),
            relativePath,
            ProjectApplicationPluginPackageReferenceContract.ValidateAndOrder(
                applicationFile.PluginPackageReferences));
    }

    private static async ValueTask<AutomationProjectManifest> LoadCurrentAsync(
        string projectRoot,
        string projectFilePath,
        CancellationToken cancellationToken)
    {
        var projectFile = await ReadAsync<AutomationProjectFile>(projectFilePath, cancellationToken)
            .ConfigureAwait(false);
        ValidateProjectFile(projectFile, projectFilePath);

        var references = projectFile.Applications;
        EnsureUnique(
            references.Select(reference => reference.ApplicationId),
            "Application ids",
            StringComparer.OrdinalIgnoreCase);
        EnsureUnique(
            references.Select(reference => reference.ProjectFile),
            "Application project paths",
            StringComparer.OrdinalIgnoreCase);

        var applications = new List<ProjectApplicationManifest>(references.Length);
        foreach (var reference in references.OrderBy(item => item.ApplicationId, StringComparer.Ordinal))
        {
            AutomationProjectFileConvention.ValidateApplicationProjectRelativePath(reference.ProjectFile);
            var applicationFilePath = AutomationProjectFileConvention.ResolveApplicationProjectPath(
                projectRoot,
                reference.ProjectFile);
            if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    applicationFilePath,
                    $"Application {reference.ApplicationId} project file"))
            {
                throw new InvalidDataException(
                    $"Application {reference.ApplicationId} project file '{reference.ProjectFile}' does not exist.");
            }

            var applicationFile = await ReadAsync<AutomationApplicationProjectFile>(
                    applicationFilePath,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateApplicationFile(reference, applicationFile, applicationFilePath);
            applications.Add(new ProjectApplicationManifest(
                applicationFile.ApplicationId,
                applicationFile.DisplayName,
                applicationFile.TopologyId,
                NormalizeStrings(applicationFile.ProcessDefinitionIds),
                reference.ProjectFile,
                ProjectApplicationPluginPackageReferenceContract.ValidateAndOrder(
                    applicationFile.PluginPackageReferences)));
        }

        var snapshots = projectFile.Snapshots
            .Select(snapshot => FromProjectFileSnapshot(snapshot, projectFile.ProjectId))
            .ToArray();

        return new AutomationProjectManifest(
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectManifest.ProductName,
            projectFile.ProjectId,
            projectFile.DisplayName,
            projectRoot,
            projectFile.CreatedAtUtc,
            projectFile.UpdatedAtUtc,
            projectFile.ActiveSnapshotId,
            applications.ToArray(),
            snapshots);
    }

    private static AutomationProjectManifest NormalizeForSave(
        AutomationProjectManifest manifest,
        string projectRoot)
    {
        if (manifest.FormatVersion != AutomationProjectManifest.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Project manifest format version {manifest.FormatVersion} cannot be saved by this version.");
        }

        if (!string.Equals(manifest.Product, AutomationProjectManifest.ProductName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Project product '{manifest.Product}' is not supported.");
        }

        if (manifest.Applications is null || manifest.Snapshots is null)
        {
            throw new InvalidDataException(
                "Project manifest must contain applications and snapshots collections.");
        }

        var applications = manifest.Applications
            .Select(application =>
            {
                var projectFilePath = application.ProjectFilePath;
                AutomationProjectFileConvention.ValidateApplicationProjectRelativePath(projectFilePath);
                if (application.ProcessDefinitionIds is null)
                {
                    throw new InvalidDataException(
                        $"Application {application.ApplicationId} must contain process definition ids.");
                }

                if (application.PluginPackageReferences is null)
                {
                    throw new InvalidDataException(
                        $"Application {application.ApplicationId} must contain pluginPackageReferences.");
                }

                return application with
                {
                    ProjectFilePath = projectFilePath,
                    ProcessDefinitionIds = NormalizeStrings(application.ProcessDefinitionIds),
                    PluginPackageReferences = ProjectApplicationPluginPackageReferenceContract
                        .ValidateAndOrder(application.PluginPackageReferences)
                };
            })
            .OrderBy(application => application.ApplicationId, StringComparer.Ordinal)
            .ToArray();

        EnsureUnique(
            applications.Select(application => application.ApplicationId),
            "Application ids",
            StringComparer.OrdinalIgnoreCase);
        EnsureUnique(
            applications.Select(application => application.ProjectFilePath),
            "Application project paths",
            StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in manifest.Snapshots)
        {
            if (snapshot is null)
            {
                throw new InvalidDataException("Project manifest contains an empty snapshot entry.");
            }

            if (!string.Equals(snapshot.ProjectId, manifest.ProjectId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Snapshot {snapshot.SnapshotId} references project {snapshot.ProjectId}, not {manifest.ProjectId}.");
            }

            if (snapshot.LayoutIds is null
                || snapshot.CapabilityBindings is null
                || snapshot.TargetReferences is null
                || snapshot.BlockVersionIds is null)
            {
                throw new InvalidDataException(
                    $"Snapshot {snapshot.SnapshotId} must contain all frozen resource collections.");
            }

            ValidateSnapshotFile(ToProjectFileSnapshot(snapshot), manifest.ProjectPath);
        }

        return manifest with
        {
            ProjectPath = projectRoot,
            Applications = applications,
            Snapshots = manifest.Snapshots
        };
    }

    private static void ValidateProjectFile(AutomationProjectFile projectFile, string projectFilePath)
    {
        if (!string.Equals(
                projectFile.SchemaVersion,
                AutomationProjectFile.CurrentSchemaVersion,
                StringComparison.Ordinal)
            || projectFile.FormatVersion != AutomationProjectManifest.CurrentFormatVersion
            || !string.Equals(projectFile.Kind, AutomationProjectFile.KindName, StringComparison.Ordinal)
            || !string.Equals(projectFile.Product, AutomationProjectManifest.ProductName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Project file '{projectFilePath}' has an unsupported schema, kind, product, or format version.");
        }

        if (string.IsNullOrWhiteSpace(projectFile.ProjectId)
            || string.IsNullOrWhiteSpace(projectFile.DisplayName))
        {
            throw new InvalidDataException($"Project file '{projectFilePath}' has an invalid identity.");
        }

        if (projectFile.Applications is null || projectFile.Snapshots is null)
        {
            throw new InvalidDataException(
                $"Project file '{projectFilePath}' must contain applications and snapshots collections.");
        }

        foreach (var snapshot in projectFile.Snapshots)
        {
            if (snapshot is null)
            {
                throw new InvalidDataException(
                    $"Project file '{projectFilePath}' contains an empty snapshot entry.");
            }

            ValidateSnapshotFile(snapshot, projectFilePath);
        }
    }

    private static void ValidateApplicationFile(
        AutomationProjectApplicationReference reference,
        AutomationApplicationProjectFile applicationFile,
        string applicationFilePath)
    {
        if (!string.Equals(
                applicationFile.SchemaVersion,
                AutomationApplicationProjectFile.CurrentSchemaVersion,
                StringComparison.Ordinal)
            || applicationFile.FormatVersion != AutomationApplicationProjectFile.CurrentFormatVersion
            || applicationFile.ResourceLayoutVersion != AutomationApplicationProjectFile.CurrentResourceLayoutVersion
            || !string.Equals(applicationFile.Kind, AutomationApplicationProjectFile.KindName, StringComparison.Ordinal)
            || !string.Equals(applicationFile.Product, AutomationProjectManifest.ProductName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Application project file '{applicationFilePath}' has an unsupported schema, kind, product, or format version.");
        }

        if (!string.Equals(applicationFile.ApplicationId, reference.ApplicationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Application project file '{applicationFilePath}' identity does not match its root project reference.");
        }

        if (string.IsNullOrWhiteSpace(applicationFile.DisplayName))
        {
            throw new InvalidDataException(
                $"Application project file '{applicationFilePath}' has an empty display name.");
        }

        if (applicationFile.ProcessDefinitionIds is null)
        {
            throw new InvalidDataException(
                $"Application project file '{applicationFilePath}' must contain processDefinitionIds.");
        }

        if (applicationFile.PluginPackageReferences is null)
        {
            throw new InvalidDataException(
                $"Application project file '{applicationFilePath}' must contain pluginPackageReferences.");
        }

        EnsureUnique(
            applicationFile.ProcessDefinitionIds,
            $"Application {applicationFile.ApplicationId} process definition ids",
            StringComparer.Ordinal);
        _ = ProjectApplicationPluginPackageReferenceContract.ValidateAndOrder(
            applicationFile.PluginPackageReferences);
    }

    public async ValueTask<IReadOnlyCollection<ProjectApplicationPluginPackageReference>> ReadAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = await ApplicationFileLocks.AcquireAsync(
                scope.ApplicationProjectFilePath,
                cancellationToken)
            .ConfigureAwait(false);
        var applicationFile = await ReadApplicationFileAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        return ProjectApplicationPluginPackageReferenceContract.ValidateAndOrder(
            applicationFile.PluginPackageReferences);
    }

    public async ValueTask ReplaceAsync(
        ProjectApplicationWorkspaceScope scope,
        IReadOnlyCollection<ProjectApplicationPluginPackageReference> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(references);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ProjectApplicationPluginPackageReferenceContract.ValidateAndOrder(references);
        using var lease = await ApplicationFileLocks.AcquireAsync(
                scope.ApplicationProjectFilePath,
                cancellationToken)
            .ConfigureAwait(false);
        var applicationFile = await ReadApplicationFileAsync(scope, cancellationToken)
            .ConfigureAwait(false);
        await WriteAtomicallyAsync(
                scope.ApplicationProjectFilePath,
                applicationFile with { PluginPackageReferences = normalized },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<AutomationApplicationProjectFile> ReadApplicationFileAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken)
    {
        if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                scope.ApplicationProjectFilePath,
                "Application project file"))
        {
            throw new FileNotFoundException(
                $"Application project file '{scope.ApplicationProjectFilePath}' does not exist.",
                scope.ApplicationProjectFilePath);
        }

        var applicationFile = await ReadAsync<AutomationApplicationProjectFile>(
                scope.ApplicationProjectFilePath,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateApplicationFile(
            new AutomationProjectApplicationReference(
                scope.ApplicationId,
                scope.ApplicationProjectRelativePath),
            applicationFile,
            scope.ApplicationProjectFilePath);
        return applicationFile;
    }

    private static void ValidateSnapshotFile(
        AutomationProjectSnapshotFile snapshot,
        string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ProductionLineDefinitionId))
        {
            throw new InvalidDataException(
                $"Project file '{projectFilePath}' snapshot '{snapshot.SnapshotId}' is missing productionLineDefinitionId.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.ReleaseManifestPath))
        {
            throw new InvalidDataException(
                $"Project file '{projectFilePath}' snapshot '{snapshot.SnapshotId}' is missing releaseManifestPath.");
        }

        if (snapshot.LayoutIds is null
            || snapshot.CapabilityBindings is null
            || snapshot.TargetReferences is null
            || snapshot.BlockVersionIds is null)
        {
            throw new InvalidDataException(
                $"Project file '{projectFilePath}' snapshot '{snapshot.SnapshotId}' must contain all frozen resource collections.");
        }

        ValidateCanonicalProjectRelativePath(snapshot.ReleaseManifestPath, "releaseManifestPath", projectFilePath);
        if (string.IsNullOrWhiteSpace(snapshot.ReleaseContentSha256)
            || snapshot.ReleaseContentSha256.Length != 64
            || !snapshot.ReleaseContentSha256.All(Uri.IsHexDigit)
            || !string.Equals(
                snapshot.ReleaseContentSha256,
                snapshot.ReleaseContentSha256.ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Project file '{projectFilePath}' snapshot '{snapshot.SnapshotId}' releaseContentSha256 must be a lowercase 64-character SHA-256 value.");
        }
    }

    private static void ValidateCanonicalProjectRelativePath(
        string relativePath,
        string fieldName,
        string projectFilePath)
    {
        if (Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\')
            || !string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal)
            || relativePath.Split('/').Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"Project file '{projectFilePath}' field '{fieldName}' must be a canonical forward-slash relative path.");
        }
    }

    private static AutomationProjectSnapshotFile ToProjectFileSnapshot(
        PublishedProjectSnapshotManifest snapshot)
    {
        return new AutomationProjectSnapshotFile(
            snapshot.SnapshotId,
            snapshot.ApplicationId,
            snapshot.TopologyId,
            NormalizeStrings(snapshot.LayoutIds),
            snapshot.ProductionLineDefinitionId,
            snapshot.PublishedAtUtc,
            snapshot.CapabilityBindings,
            snapshot.TargetReferences,
            NormalizeStrings(snapshot.BlockVersionIds),
            snapshot.ReleaseManifestPath,
            snapshot.ReleaseContentSha256);
    }

    private static PublishedProjectSnapshotManifest FromProjectFileSnapshot(
        AutomationProjectSnapshotFile snapshot,
        string projectId)
    {
        return new PublishedProjectSnapshotManifest(
            snapshot.SnapshotId,
            projectId,
            snapshot.ApplicationId,
            snapshot.TopologyId,
            NormalizeStrings(snapshot.LayoutIds),
            snapshot.ProductionLineDefinitionId,
            snapshot.PublishedAtUtc,
            snapshot.CapabilityBindings,
            snapshot.TargetReferences,
            NormalizeStrings(snapshot.BlockVersionIds),
            snapshot.ReleaseManifestPath,
            snapshot.ReleaseContentSha256);
    }

    private static async ValueTask<T> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    path,
                    "Project file"))
            {
                throw new FileNotFoundException(
                    $"Project file '{path}' does not exist.",
                    path);
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new InvalidDataException($"Project file '{path}' is empty or invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Project file '{path}' contains invalid JSON: {exception.Message}",
                exception);
        }
    }

    private static async ValueTask WriteAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException($"Project file '{path}' has no parent directory.");
        ProjectWorkspacePathGuard.CreateOrdinaryDirectory(directory, "Project file directory");
        var targetExists = ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
            path,
            "Project file");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (targetExists)
        {
            var existingBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (existingBytes.AsSpan().SequenceEqual(bytes))
            {
                return;
            }
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        Exception? operationFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                temporaryPath,
                "Temporary project file");
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                temporaryPath,
                "Temporary project file");
            ProjectWorkspacePathGuard.EnsureOrdinaryDirectoryOrMissing(
                directory,
                "Project file directory");
            ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                path,
                "Project file");
            File.Move(temporaryPath, path, overwrite: true);
            if (!ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    path,
                    "Project file"))
            {
                throw new InvalidDataException(
                    $"Project file '{path}' was not persisted as an ordinary file.");
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        try
        {
            if (ProjectWorkspacePathGuard.EnsureOrdinaryFileOrMissing(
                    temporaryPath,
                    "Temporary project file"))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        ProjectWorkspaceFileOperation.ThrowFailures(
            "Project file commit and temporary-file cleanup both failed.",
            operationFailure,
            cleanupFailure);
    }

    private static string[] FindProjectFiles(string projectRoot)
    {
        if (!Directory.Exists(projectRoot))
        {
            return [];
        }

        var projectFiles = Directory.EnumerateFiles(projectRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(
                AutomationProjectFileConvention.ProjectFileExtension,
                StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nonCanonical = projectFiles.FirstOrDefault(path => !path.EndsWith(
            AutomationProjectFileConvention.ProjectFileExtension,
            StringComparison.Ordinal));
        if (nonCanonical is not null)
        {
            throw new InvalidDataException(
                $"Project file '{nonCanonical}' must use the canonical lowercase "
                + $"{AutomationProjectFileConvention.ProjectFileExtension} extension.");
        }

        return projectFiles;
    }

    private static bool IsProjectFilePath(string path)
    {
        return path.EndsWith(
            AutomationProjectFileConvention.ProjectFileExtension,
            StringComparison.Ordinal);
    }

    private static void RejectNonCanonicalProjectFileExtension(string path)
    {
        if (path.EndsWith(
                AutomationProjectFileConvention.ProjectFileExtension,
                StringComparison.OrdinalIgnoreCase)
            && !IsProjectFilePath(path))
        {
            throw new InvalidDataException(
                $"Project target '{path}' must use the canonical lowercase "
                + $"{AutomationProjectFileConvention.ProjectFileExtension} extension.");
        }
    }

    private static string GetApplicationProjectRelativePath(
        string projectRoot,
        string applicationProjectTarget)
    {
        if (string.IsNullOrWhiteSpace(applicationProjectTarget))
        {
            throw new ArgumentException(
                "Application project target cannot be empty.",
                nameof(applicationProjectTarget));
        }

        var fullPath = Path.IsPathRooted(applicationProjectTarget)
            ? Path.GetFullPath(applicationProjectTarget.Trim())
            : Path.GetFullPath(Path.Combine(
                projectRoot,
                applicationProjectTarget.Trim().Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, PathComparison))
        {
            throw new InvalidDataException(
                "An imported Application project must be copied inside the Automation Project directory first.");
        }

        return AutomationProjectFileConvention.NormalizeDocumentPath(
            Path.GetRelativePath(root, fullPath));
    }

    private static (string Path, string Description)[] GetApplicationDirectories(
        string applicationRoot)
    {
        return
        [
            (applicationRoot, "Application root"),
            (Path.Combine(applicationRoot, "topology"), "Application topology directory"),
            (Path.Combine(applicationRoot, "layouts"), "Application layouts directory"),
            (Path.Combine(applicationRoot, "flows"), "Application flows directory"),
            (
                Path.Combine(applicationRoot, "blocks", "custom"),
                "Application custom blocks directory"),
            (
                Path.Combine(applicationRoot, "configuration"),
                "Application configuration directory"),
            (
                Path.Combine(
                    applicationRoot,
                    ProjectApplicationPluginPackageReferenceContract.PluginsDirectoryName),
                "Application plugins directory")
        ];
    }

    private static string[] NormalizeStrings(IEnumerable<string> values)
    {
        return values
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void EnsureUnique(
        IEnumerable<string> values,
        string description,
        StringComparer comparer)
    {
        var set = new HashSet<string>(comparer);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !set.Add(value))
            {
                throw new InvalidDataException($"{description} must be non-empty and unique.");
            }
        }
    }

}
