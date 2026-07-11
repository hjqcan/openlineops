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

    ValueTask<ExternalProgramResource> SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        SaveExternalProgramResourceRequest request,
        IReadOnlyCollection<ExternalProgramFileUpload> uploads,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalProgramResource> ImportFileAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        ExternalProgramFileUpload upload,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        ProjectApplicationWorkspaceScope scope,
        string resourceId,
        CancellationToken cancellationToken = default);
}
