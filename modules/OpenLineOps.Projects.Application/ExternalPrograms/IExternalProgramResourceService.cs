using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Projects.Application.ExternalPrograms;

public interface IExternalProgramResourceService
{
    Task<Result<IReadOnlyCollection<ExternalProgramResource>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<Result<ExternalProgramResource>> GetAsync(
        string projectId,
        string applicationId,
        string resourceId,
        CancellationToken cancellationToken = default);

    Task<Result<ExternalProgramResource>> SaveAsync(
        string projectId,
        string applicationId,
        SaveExternalProgramResourceRequest request,
        IReadOnlyCollection<ExternalProgramFileUpload> uploads,
        CancellationToken cancellationToken = default);

    Task<Result<ExternalProgramResource>> ImportFileAsync(
        string projectId,
        string applicationId,
        string resourceId,
        ExternalProgramFileUpload upload,
        CancellationToken cancellationToken = default);

    Task<Result<bool>> DeleteAsync(
        string projectId,
        string applicationId,
        string resourceId,
        CancellationToken cancellationToken = default);

    Task<Result<ExternalProgramProtocolTrialResult>> TrialAsync(
        string projectId,
        string applicationId,
        string resourceId,
        ExternalProgramProtocolTrialRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ExternalProgramProtocolTrialResult>> TrialDefinitionAsync(
        string projectId,
        string applicationId,
        SaveExternalProgramResourceRequest definition,
        ExternalProgramProtocolTrialRequest request,
        CancellationToken cancellationToken = default);
}
