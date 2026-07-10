using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Projects.Domain.Identifiers;

namespace OpenLineOps.Projects.Api.Integrations;

public sealed class ProjectReleasePluginCommandResolver : IProjectReleasePluginCommandResolver
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly IAutomationProjectRepository _projectRepository;
    private readonly IProjectReleaseArtifactStore _releaseStore;

    public ProjectReleasePluginCommandResolver(
        IAutomationProjectRepository projectRepository,
        IProjectReleaseArtifactStore releaseStore)
    {
        _projectRepository = projectRepository;
        _releaseStore = releaseStore;
    }

    public async ValueTask<ProjectReleasePluginCommand?> ResolveAsync(
        string projectId,
        string applicationId,
        string snapshotId,
        string capabilityId,
        string commandName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(applicationId)
            || string.IsNullOrWhiteSpace(snapshotId)
            || string.IsNullOrWhiteSpace(capabilityId)
            || string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        var project = await _projectRepository
            .GetByIdAsync(new AutomationProjectId(projectId), cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        var application = project.Applications.SingleOrDefault(candidate =>
            string.Equals(candidate.Id.Value, applicationId, StringComparison.Ordinal));
        var snapshot = project.Snapshots.SingleOrDefault(candidate =>
            string.Equals(candidate.Id.Value, snapshotId, StringComparison.Ordinal)
            && string.Equals(candidate.ApplicationId.Value, applicationId, StringComparison.Ordinal));
        if (application is null || snapshot is null)
        {
            return null;
        }

        var scope = new ProjectApplicationWorkspaceScope(
            project.Id.Value,
            application.Id.Value,
            project.ProjectPath,
            application.ProjectFilePath);
        OpenedProjectReleaseArtifact? release;
        try
        {
            release = await _releaseStore
                .OpenAsync(scope, snapshot.Id.Value, snapshot.ReleaseContentSha256, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return null;
        }

        if (release is null)
        {
            return null;
        }

        var matches = release.Metadata.PackageDependencies
            .SelectMany(dependency => dependency.Commands
                .Where(command => string.Equals(command.Kind, "Process", StringComparison.Ordinal)
                                  && string.Equals(command.CapabilityId, capabilityId, StringComparison.Ordinal)
                                  && string.Equals(command.CommandName, commandName, StringComparison.OrdinalIgnoreCase))
                .Select(command => new { Dependency = dependency, Command = command }))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        var match = matches[0];
        var packagePath = ResolveInsideRelease(release.ReleaseRootPath, match.Dependency.PackageRelativePath);
        return Directory.Exists(packagePath)
            ? new ProjectReleasePluginCommand(
                match.Dependency.PluginId,
                match.Dependency.PackageVersion,
                match.Dependency.PackageContentSha256,
                match.Dependency.ManifestSha256,
                match.Dependency.EntryAssemblySha256,
                match.Dependency.ContractVersion,
                match.Dependency.RuntimeIdentifier,
                match.Dependency.AbiVersion,
                packagePath,
                match.Command.CommandDefinitionId,
                match.Command.CapabilityId,
                match.Command.CommandName)
            : null;
    }

    private static string ResolveInsideRelease(string releaseRootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
        {
            throw new InvalidDataException("Release package path is not canonical.");
        }

        var root = Path.GetFullPath(releaseRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException("Release package path escapes the release root.");
        }

        return path;
    }
}
