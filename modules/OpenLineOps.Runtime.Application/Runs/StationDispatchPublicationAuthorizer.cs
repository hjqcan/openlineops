using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public sealed record StationDispatchPublicationDecision(bool Allowed, string? RejectionReason)
{
    public static StationDispatchPublicationDecision Allow() => new(true, null);

    public static StationDispatchPublicationDecision Reject(string reason) =>
        new(false, string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("Dispatch rejection reason is required.", nameof(reason))
            : reason);
}

public sealed class StationDispatchPublicationAuthorizer(
    IProductionRunRepository runs,
    IResourceLeaseRepository resourceLeases,
    IStationDeploymentResolver deployments,
    IClock clock)
{
    public async ValueTask<StationDispatchPublicationDecision> AuthorizeAsync(
        StationJobRequested request,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(request);
        var entry = await runs.GetByIdAsync(
                new ProductionRunId(request.ProductionRunId),
                cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return StationDispatchPublicationDecision.Reject(
                "Station dispatch references a Production Run that no longer exists.");
        }

        var run = entry.Run.ToSnapshot();
        if (run.ExecutionStatus != ExecutionStatus.Running
            || run.ControlState != ProductionRunControlState.Active
            || run.ProductionUnitId.Value != request.ProductionUnitId
            || !string.Equals(run.ProjectId, request.ProjectId, StringComparison.Ordinal)
            || !string.Equals(run.ApplicationId, request.ApplicationId, StringComparison.Ordinal)
            || !string.Equals(run.ProjectSnapshotId, request.ProjectSnapshotId, StringComparison.Ordinal)
            || !string.Equals(
                run.ProductionLineDefinitionId,
                request.ProductionLineDefinitionId,
                StringComparison.Ordinal)
            || !string.Equals(run.TopologyId, request.TopologyId, StringComparison.Ordinal))
        {
            return StationDispatchPublicationDecision.Reject(
                "Production Run is not Active/Running or no longer matches the Station dispatch identity.");
        }

        var operation = run.Operations.SingleOrDefault(item => string.Equals(
            item.OperationRunId,
            request.OperationRunId,
            StringComparison.Ordinal));
        if (operation is null
            || operation.ExecutionStatus != ExecutionStatus.Running
            || operation.RuntimeSessionId?.Value != request.RuntimeSessionId
            || operation.Attempt != request.OperationAttempt
            || !string.Equals(operation.Definition.OperationId, request.OperationId, StringComparison.Ordinal)
            || !string.Equals(
                operation.Definition.StationSystemId,
                request.StationSystemId,
                StringComparison.Ordinal)
            || !string.Equals(
                operation.Definition.ProcessDefinitionId.Value,
                request.FlowDefinitionId,
                StringComparison.Ordinal)
            || !string.Equals(
                operation.Definition.ProcessVersionId.Value,
                request.FlowVersionId,
                StringComparison.Ordinal)
            || !string.Equals(
                operation.Definition.ConfigurationSnapshotId.Value,
                request.ConfigurationSnapshotId,
                StringComparison.Ordinal)
            || !string.Equals(
                operation.Definition.RecipeSnapshotId.Value,
                request.RecipeSnapshotId,
                StringComparison.Ordinal))
        {
            return StationDispatchPublicationDecision.Reject(
                "Station dispatch Operation Run is not the exact durable Running operation.");
        }

        StationDeploymentRoute route;
        try
        {
            route = await deployments.ResolveAsync(
                    new StationDeploymentRequest(
                        request.ProjectId,
                        request.ApplicationId,
                        request.ProjectSnapshotId,
                        request.StationSystemId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or InvalidOperationException
                                           or ArgumentException)
        {
            return StationDispatchPublicationDecision.Reject(
                $"Exact signed Station deployment is unavailable or invalid: {exception.Message}");
        }
        if (!string.Equals(route.AgentId, request.AgentId, StringComparison.Ordinal)
            || !string.Equals(route.StationId, request.StationId, StringComparison.Ordinal)
            || !string.Equals(
                route.PackageContentSha256,
                request.PackageContentSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                route.ProductionLineDefinitionId,
                request.ProductionLineDefinitionId,
                StringComparison.Ordinal))
        {
            return StationDispatchPublicationDecision.Reject(
                "Station dispatch no longer matches the exact signed Station deployment.");
        }

        var evidence = request.ResourceFences.Select(fence => new ResourceLeaseFenceEvidence(
                new ResourceRequirement(
                    Enum.Parse<ResourceKind>(fence.ResourceKind, ignoreCase: false),
                    fence.ResourceId),
                fence.FencingToken,
                fence.ExpiresAtUtc))
            .ToArray();
        var leaseValidation = await resourceLeases.ValidateCurrentAsync(
                run.RunId,
                request.OperationRunId,
                evidence,
                RequireUtc(clock.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
        return leaseValidation.Accepted
            ? StationDispatchPublicationDecision.Allow()
            : StationDispatchPublicationDecision.Reject(
                leaseValidation.RejectionReason
                ?? "Station dispatch resource leases are no longer current.");
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value == default || value.Offset != TimeSpan.Zero
            ? throw new InvalidOperationException(
                "Station dispatch publication clock must return non-default UTC.")
            : value;
}
