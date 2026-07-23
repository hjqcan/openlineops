using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Projects.Application.ExternalPrograms;

public interface IExternalProgramResourceRepository
{
    ValueTask<IReadOnlyCollection<ExternalProgramResource>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalProgramResource?> GetAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalProgramResource> SaveDefinitionAsync(
        ProjectApplicationWorkspaceScope scope,
        SaveExternalProgramResourceRequest request,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalProgramResource> ImportDirectoryAsync(
        ProjectApplicationWorkspaceScope scope,
        SaveExternalProgramResourceRequest request,
        IReadOnlyCollection<ExternalProgramFileUpload> files,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        CancellationToken cancellationToken = default);
}
