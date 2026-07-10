using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Application.Projects;

namespace OpenLineOps.Projects.Application.Releases;

public interface IProjectReleasePublisher
{
    Task<Result<AutomationProjectDetails>> PublishAsync(
        string projectId,
        PublishProjectReleaseRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PublishProjectReleaseRequest(
    string SnapshotId,
    string ApplicationId,
    string ProductionLineDefinitionId);
