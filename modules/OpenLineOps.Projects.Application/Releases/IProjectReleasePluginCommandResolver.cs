namespace OpenLineOps.Projects.Application.Releases;

public interface IProjectReleasePluginCommandResolver
{
    ValueTask<ProjectReleasePluginCommand?> ResolveAsync(
        string projectId,
        string applicationId,
        string snapshotId,
        string capabilityId,
        string commandName,
        string? targetKind = null,
        string? targetId = null,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectReleasePluginCommand(
    string PluginId,
    string PackageVersion,
    string PackageContentSha256,
    string ManifestSha256,
    string EntryAssemblySha256,
    string ContractVersion,
    string RuntimeIdentifier,
    string AbiVersion,
    string PackagePath,
    string CommandDefinitionId,
    string CapabilityId,
    string CommandName);
