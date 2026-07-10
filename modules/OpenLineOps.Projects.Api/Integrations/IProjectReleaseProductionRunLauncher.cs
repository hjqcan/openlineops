using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Projects.Api.Integrations;

public interface IProjectReleaseProductionRunLauncher
{
    ValueTask<Result<ProductionRunRunResult>> StartAsync(
        PublishedProjectSnapshotDetails snapshot,
        StartProjectReleaseProductionRunRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StartProjectReleaseProductionRunRequest(
    Guid ProductionRunId,
    string DutIdentityValue,
    string ActorId,
    string? BatchId = null,
    string? FixtureId = null,
    string? DeviceId = null);
