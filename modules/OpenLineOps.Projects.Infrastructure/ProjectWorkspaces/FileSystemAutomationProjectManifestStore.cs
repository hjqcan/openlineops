using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;

public sealed class FileSystemAutomationProjectManifestStore : IAutomationProjectManifestStore
{
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
        if (IsProjectFilePath(fullPath))
        {
            return Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException($"Project file '{fullPath}' has no parent directory.");
        }

        if (File.Exists(fullPath))
        {
            throw new InvalidDataException(
                $"Project target '{fullPath}' must be a directory, {AutomationProjectFileConvention.ProjectFileExtension}, or {AutomationProjectFileConvention.LegacyProjectFileName} file.");
        }

        return fullPath;
    }

    public string GetManifestPath(string projectTarget, string? projectId = null)
    {
        var fullTarget = Path.GetFullPath(projectTarget.Trim());
        if (IsProjectFilePath(fullTarget))
        {
            return fullTarget;
        }

        var projectRoot = GetProjectRootPath(fullTarget);
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

        var legacyPath = Path.Combine(projectRoot, AutomationProjectFileConvention.LegacyProjectFileName);
        if (File.Exists(legacyPath))
        {
            return legacyPath;
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
        Directory.CreateDirectory(projectRoot);
        EnsureDirectoryIsNotReparsePoint(projectRoot, "Project root");

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

        foreach (var application in normalized.Applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applicationFilePath = AutomationProjectFileConvention.ResolveApplicationProjectPath(
                projectRoot,
                application.ProjectFilePath!);
            EnsureProjectPathHasNoReparsePoints(projectRoot, application.ProjectFilePath!);
            CreateApplicationDirectories(Path.GetDirectoryName(applicationFilePath)!);

            var applicationFile = new AutomationApplicationProjectFile(
                AutomationApplicationProjectFile.CurrentSchemaVersion,
                AutomationApplicationProjectFile.CurrentFormatVersion,
                AutomationApplicationProjectFile.KindName,
                AutomationProjectManifest.ProductName,
                application.ApplicationId,
                application.DisplayName,
                AutomationApplicationProjectFile.CurrentResourceLayoutVersion,
                application.TopologyId,
                application.ProcessDefinitionIds);

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
                    application.ProjectFilePath!))
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
        var projectFilePath = GetManifestPath(projectTarget);
        if (!File.Exists(projectFilePath))
        {
            return null;
        }

        EnsureDirectoryIsNotReparsePoint(projectRoot, "Project root");
        return string.Equals(
            Path.GetFileName(projectFilePath),
            AutomationProjectFileConvention.LegacyProjectFileName,
            StringComparison.OrdinalIgnoreCase)
            ? await LoadLegacyAsync(projectRoot, projectFilePath, cancellationToken).ConfigureAwait(false)
            : await LoadCurrentAsync(projectRoot, projectFilePath, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<AutomationProjectManifest> LoadCurrentAsync(
        string projectRoot,
        string projectFilePath,
        CancellationToken cancellationToken)
    {
        var projectFile = await ReadAsync<AutomationProjectFile>(projectFilePath, cancellationToken)
            .ConfigureAwait(false);
        ValidateProjectFile(projectFile, projectFilePath);

        var references = projectFile.Applications ?? [];
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
            EnsureProjectPathHasNoReparsePoints(projectRoot, reference.ProjectFile);
            var applicationFilePath = AutomationProjectFileConvention.ResolveApplicationProjectPath(
                projectRoot,
                reference.ProjectFile);
            if (!File.Exists(applicationFilePath))
            {
                throw new InvalidDataException(
                    $"Application {reference.ApplicationId} project file '{reference.ProjectFile}' does not exist.");
            }

            var applicationFile = await ReadAsync<AutomationApplicationProjectFile>(
                    applicationFilePath,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateApplicationFile(projectFile, reference, applicationFile, applicationFilePath);
            applications.Add(new ProjectApplicationManifest(
                applicationFile.ApplicationId,
                applicationFile.DisplayName,
                applicationFile.TopologyId,
                NormalizeStrings(applicationFile.ProcessDefinitionIds),
                reference.ProjectFile));
        }

        var snapshots = (projectFile.Snapshots ?? [])
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

    private static async ValueTask<AutomationProjectManifest> LoadLegacyAsync(
        string projectRoot,
        string projectFilePath,
        CancellationToken cancellationToken)
    {
        var legacy = await ReadAsync<LegacyAutomationProjectManifest>(projectFilePath, cancellationToken)
            .ConfigureAwait(false);
        if (legacy.FormatVersion != AutomationProjectManifest.LegacyFormatVersion
            || !string.Equals(legacy.Product, AutomationProjectManifest.ProductName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Legacy project manifest '{projectFilePath}' has an unsupported format or product.");
        }

        var applications = (legacy.Applications ?? [])
            .Select(application => new ProjectApplicationManifest(
                application.ApplicationId,
                application.DisplayName,
                application.TopologyId,
                NormalizeStrings(application.ProcessDefinitionIds),
                AutomationProjectFileConvention.GetLegacyApplicationProjectRelativePath(
                    application.ApplicationId)))
            .ToArray();

        return new AutomationProjectManifest(
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectManifest.ProductName,
            legacy.ProjectId,
            legacy.DisplayName,
            projectRoot,
            legacy.CreatedAtUtc,
            legacy.UpdatedAtUtc,
            legacy.ActiveSnapshotId,
            applications,
            legacy.Snapshots ?? []);
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

        var applications = (manifest.Applications ?? [])
            .Select(application =>
            {
                var projectFilePath = application.ProjectFilePath
                    ?? AutomationProjectFileConvention.GetApplicationProjectRelativePath(application.ApplicationId);
                AutomationProjectFileConvention.ValidateApplicationProjectRelativePath(projectFilePath);
                return application with
                {
                    ProjectFilePath = projectFilePath,
                    ProcessDefinitionIds = NormalizeStrings(application.ProcessDefinitionIds)
                };
            })
            .OrderBy(application => application.ApplicationId, StringComparer.Ordinal)
            .ToArray();

        EnsureUnique(
            applications.Select(application => application.ApplicationId),
            "Application ids",
            StringComparer.OrdinalIgnoreCase);
        EnsureUnique(
            applications.Select(application => application.ProjectFilePath!),
            "Application project paths",
            StringComparer.OrdinalIgnoreCase);

        return manifest with
        {
            ProjectPath = projectRoot,
            Applications = applications,
            Snapshots = manifest.Snapshots ?? []
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
    }

    private static void ValidateApplicationFile(
        AutomationProjectFile projectFile,
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

        EnsureUnique(
            applicationFile.ProcessDefinitionIds ?? [],
            $"Application {applicationFile.ApplicationId} process definition ids",
            StringComparer.Ordinal);
    }

    private static AutomationProjectSnapshotFile ToProjectFileSnapshot(
        PublishedProjectSnapshotManifest snapshot)
    {
        return new AutomationProjectSnapshotFile(
            snapshot.SnapshotId,
            snapshot.ApplicationId,
            snapshot.TopologyId,
            NormalizeStrings(snapshot.LayoutIds),
            snapshot.ProcessDefinitionId,
            snapshot.ProcessVersionId,
            snapshot.ConfigurationSnapshotId,
            snapshot.PublishedAtUtc,
            snapshot.CapabilityBindings ?? [],
            snapshot.TargetReferences ?? [],
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
            snapshot.ProcessDefinitionId,
            snapshot.ProcessVersionId,
            snapshot.ConfigurationSnapshotId,
            snapshot.PublishedAtUtc,
            snapshot.CapabilityBindings ?? [],
            snapshot.TargetReferences ?? [],
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
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string[] FindProjectFiles(string projectRoot)
    {
        if (!Directory.Exists(projectRoot))
        {
            return [];
        }

        return Directory.EnumerateFiles(projectRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(
                AutomationProjectFileConvention.ProjectFileExtension,
                StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsProjectFilePath(string path)
    {
        return path.EndsWith(
                   AutomationProjectFileConvention.ProjectFileExtension,
                   StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   Path.GetFileName(path),
                   AutomationProjectFileConvention.LegacyProjectFileName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateApplicationDirectories(string applicationRoot)
    {
        Directory.CreateDirectory(applicationRoot);
        Directory.CreateDirectory(Path.Combine(applicationRoot, "topology"));
        Directory.CreateDirectory(Path.Combine(applicationRoot, "layouts"));
        Directory.CreateDirectory(Path.Combine(applicationRoot, "flows"));
        Directory.CreateDirectory(Path.Combine(applicationRoot, "blocks", "custom"));
        Directory.CreateDirectory(Path.Combine(applicationRoot, "configuration"));
    }

    private static void EnsureProjectPathHasNoReparsePoints(
        string projectRoot,
        string relativePath)
    {
        var current = Path.GetFullPath(projectRoot);
        foreach (var segment in relativePath.Split('/').SkipLast(1))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
            {
                EnsureDirectoryIsNotReparsePoint(current, $"Project directory '{segment}'");
            }
        }
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path, string description)
    {
        if (Directory.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{description} cannot be a symbolic link or reparse point.");
        }
    }

    private static string[] NormalizeStrings(IEnumerable<string>? values)
    {
        return (values ?? [])
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

    private sealed record LegacyAutomationProjectManifest(
        int FormatVersion,
        string Product,
        string ProjectId,
        string DisplayName,
        string ProjectPath,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        string? ActiveSnapshotId,
        LegacyProjectApplicationManifest[] Applications,
        PublishedProjectSnapshotManifest[] Snapshots);

    private sealed record LegacyProjectApplicationManifest(
        string ApplicationId,
        string DisplayName,
        string? TopologyId,
        string[] ProcessDefinitionIds);
}
