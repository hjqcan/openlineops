using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public sealed record StationDispatchPublicationDecision(
    bool Allowed,
    string? RejectionReason,
    string? StationGateRevision,
    string? StationGateEvidence,
    string? StationGateEvidenceSha256,
    StationJobRequested? AuthorizedRequest)
{
    public static StationDispatchPublicationDecision Allow(
        StationJobRequested request,
        string stationGateRevision,
        string stationGateEvidence,
        DateTimeOffset authorizedAtUtc,
        DateTimeOffset expiresAtUtc,
        StationAgentControlLeaseDispatchAuthority agentControlLease)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationGateRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationGateEvidence);
        var authorizedRequest = StationMessageContract.BindStationExecutionGateEvidence(
            request,
            stationGateRevision,
            stationGateEvidence,
            authorizedAtUtc,
            expiresAtUtc,
            agentControlLease);
        return Allow(authorizedRequest);
    }

    public static StationDispatchPublicationDecision Allow(
        StationJobRequested authorizedRequest)
    {
        ArgumentNullException.ThrowIfNull(authorizedRequest);
        StationMessageContract.ValidateForAgentDispatch(authorizedRequest);
        return new(
            true,
            null,
            authorizedRequest.StationExecutionGateRevision,
            authorizedRequest.StationExecutionGateEvidence,
            authorizedRequest.StationExecutionGateEvidenceSha256,
            authorizedRequest);
    }

    public static StationDispatchPublicationDecision Reject(string reason) =>
        new(
            false,
            string.IsNullOrWhiteSpace(reason)
                ? throw new ArgumentException(
                    "Dispatch rejection reason is required.",
                    nameof(reason))
                : reason,
            null,
            null,
            null,
            null);
}

public sealed class StationDispatchPublicationAuthorizer(
    IProductionRunRepository runs,
    IResourceLeaseRepository resourceLeases,
    IStationDeploymentResolver deployments,
    IStationProductionExecutionGate stationExecutionGate,
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
                cancellationToken)
            .ConfigureAwait(false);
        if (!leaseValidation.Accepted)
        {
            return StationDispatchPublicationDecision.Reject(
                leaseValidation.RejectionReason
                ?? "Station dispatch resource leases are no longer current.");
        }

        var stationGate = await stationExecutionGate.EvaluateAsync(
                operation.Definition.StationSystemId,
                operation.Definition.RecipeId is null
                    ? null
                    : new StationExecutionRecipeExpectation(
                        operation.Definition.RecipeId,
                        operation.Definition.RecipeSnapshotId.Value),
                cancellationToken)
            .ConfigureAwait(false);
        if (!stationGate.Managed || !stationGate.Allowed)
        {
            return StationDispatchPublicationDecision.Reject(
                stationGate.Managed
                    ? stationGate.Reason
                    : $"Station {operation.Definition.StationSystemId} is not enrolled in the "
                      + "managed production execution gate.");
        }

        if (string.IsNullOrWhiteSpace(stationGate.Evidence))
        {
            return StationDispatchPublicationDecision.Reject(
                "Managed Station execution gate returned no auditable evidence.");
        }

        if (string.IsNullOrWhiteSpace(stationGate.Revision))
        {
            return StationDispatchPublicationDecision.Reject(
                "Managed Station execution gate returned no stable authorization revision.");
        }

        if (stationGate.AgentControlLease is not { } agentControlLease
            || !string.Equals(
                agentControlLease.OwnerAgentId,
                request.AgentId,
                StringComparison.Ordinal)
            || agentControlLease.FencingToken <= 0)
        {
            return StationDispatchPublicationDecision.Reject(
                "Managed Station execution gate returned no matching Agent control lease authority.");
        }

        var authorizedAtUtc = clock.UtcNow;
        if (stationGate.ValidUntilUtc is not { } validUntilUtc
            || validUntilUtc.Offset != TimeSpan.Zero
            || validUntilUtc <= authorizedAtUtc
            || agentControlLease.ExpiresAtUtc.Offset != TimeSpan.Zero
            || agentControlLease.ExpiresAtUtc < validUntilUtc)
        {
            return StationDispatchPublicationDecision.Reject(
                "Managed Station execution gate evidence has expired or has no validity boundary.");
        }

        if (request.StationExecutionGateEvidence is not null)
        {
            StationMessageContract.ValidateForAgentDispatch(request);
            if (request.StationExecutionGateAuthorizedAtUtc > authorizedAtUtc
                || request.StationExecutionGateExpiresAtUtc <= authorizedAtUtc
                || request.StationExecutionGateExpiresAtUtc > validUntilUtc
                || !string.Equals(
                    request.StationAgentControlLeaseOwnerAgentId,
                    agentControlLease.OwnerAgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.StationAgentControlLeaseOwnerInstanceId,
                    agentControlLease.OwnerInstanceId,
                    StringComparison.Ordinal)
                || request.StationAgentControlLeaseFencingToken
                    != agentControlLease.FencingToken
                || request.StationAgentControlLeaseExpiresAtUtc <= authorizedAtUtc
                || request.StationAgentControlLeaseExpiresAtUtc
                    > agentControlLease.ExpiresAtUtc
                || !string.Equals(
                    request.StationExecutionGateRevision,
                    stationGate.Revision,
                    StringComparison.Ordinal))
            {
                return StationDispatchPublicationDecision.Reject(
                    "Durable Station execution gate evidence is expired, exceeds the "
                    + "current gate boundary, or its stable revision changed.");
            }

            return StationDispatchPublicationDecision.Allow(request);
        }

        return StationDispatchPublicationDecision.Allow(
            request,
            stationGate.Revision,
            stationGate.Evidence,
            authorizedAtUtc,
            validUntilUtc,
            agentControlLease);
    }
}
