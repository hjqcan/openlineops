using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Projects.Api.Integrations;

public interface IProjectReleaseProductionRunLauncher
{
    ValueTask<Result<ProductionRunSnapshot>> SubmitAsync(
        PublishedProjectSnapshotDetails snapshot,
        SubmitProjectReleaseProductionRunRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SubmitProjectReleaseProductionRunRequest(
    Guid ProductionRunId,
    string ProductionUnitIdentityValue,
    string ActorId,
    string? LotId = null,
    string? CarrierId = null,
    string? SlotId = null,
    string? FixtureId = null,
    string? DeviceId = null);
