using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Runs;

public static class OperationExecutionEvidenceFactory
{
    public static OperationExecutionEvidence FromRuntimeSession(
        StationOperationDispatchRequest request,
        RuntimeSession session)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        var metadata = session.TraceMetadata;
        return new OperationExecutionEvidence(
            OperationExecutionEvidenceOrigin.RuntimeSession,
            session.Id.Value,
            metadata.ProductionRunId.Value,
            metadata.ProductionUnitId.Value,
            metadata.ProductionLineDefinitionId,
            metadata.OperationId,
            metadata.OperationRunId,
            metadata.OperationAttempt,
            metadata.StationSystemId,
            session.StationId.Value,
            session.ProcessDefinitionId.Value,
            session.ProcessVersionId.Value,
            session.ConfigurationSnapshotId.Value,
            session.RecipeSnapshotId.Value,
            metadata.ProductionUnitIdentity.ModelId,
            metadata.ProductionUnitIdentity.InputKey,
            metadata.ProductionUnitIdentity.Value,
            metadata.LotId,
            metadata.CarrierId,
            metadata.FixtureId,
            metadata.DeviceId,
            metadata.ActorId,
            metadata.ProjectId,
            metadata.ApplicationId,
            metadata.ProjectSnapshotId,
            metadata.TopologyId,
            session.Status.ToString(),
            session.CompletedAtUtc
                ?? throw new InvalidDataException(
                    $"Terminal Runtime Session {session.Id} has no completion timestamp."),
            metadata.ResourceLeaseFences.Select(fence => new OperationResourceFenceEvidence(
                fence.Resource.Kind.ToString(),
                fence.Resource.ResourceId,
                fence.FencingToken,
                fence.ExpiresAtUtc)).ToArray(),
            session.Steps.Select(step => new OperationStepExecutionEvidence(
                step.Id.Value,
                step.NodeId.Value,
                step.ActionId.Value,
                step.TargetKind,
                step.TargetId,
                step.DisplayName,
                step.Status.ToString(),
                step.StartedAtUtc,
                step.CompletedAtUtc,
                step.FailureReason)).ToArray(),
            session.Commands.Select(command => new OperationCommandExecutionEvidence(
                command.Id.Value,
                command.StepId.Value,
                session.Steps.Single(step => step.Id == command.StepId).NodeId.Value,
                command.ActionId.Value,
                command.TargetKind,
                command.TargetId,
                command.TargetCapability.Value,
                command.CommandName,
                command.Status,
                command.CreatedAtUtc,
                command.DeadlineAtUtc,
                command.AcceptedAtUtc,
                command.StartedAtUtc,
                command.CompletedAtUtc,
                command.ResultPayload,
                command.FailureReason,
                command.ResultJudgement)).ToArray(),
            session.Incidents.Select(incident => new OperationIncidentExecutionEvidence(
                incident.Id.Value,
                incident.Severity.ToString(),
                incident.Code,
                incident.Message,
                incident.OccurredAtUtc)).ToArray(),
            []);
    }

    public static OperationExecutionEvidence FromStationCompletion(
        StationOperationDispatchRequest request,
        StationJobCompleted completion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(completion);
        var run = request.Run;
        var operation = request.Operation;
        return new OperationExecutionEvidence(
            OperationExecutionEvidenceOrigin.StationAgent,
            completion.RuntimeSessionId,
            run.RunId.Value,
            run.ProductionUnitId.Value,
            run.ProductionLineDefinitionId,
            operation.Definition.OperationId,
            operation.OperationRunId,
            operation.Attempt,
            operation.Definition.StationSystemId,
            operation.Definition.StationId.Value,
            operation.Definition.ProcessDefinitionId.Value,
            operation.Definition.ProcessVersionId.Value,
            operation.Definition.ConfigurationSnapshotId.Value,
            operation.Definition.RecipeSnapshotId.Value,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            run.LotId,
            run.CarrierId,
            FindResource(operation, ResourceKind.Fixture),
            FindResource(operation, ResourceKind.Device),
            run.ActorId,
            run.ProjectId,
            run.ApplicationId,
            run.ProjectSnapshotId,
            run.TopologyId,
            ToRuntimeSessionStatus(completion.ExecutionStatus),
            completion.CompletedAtUtc,
            request.ResourceLeases.Select(lease => new OperationResourceFenceEvidence(
                lease.Resource.Kind.ToString(),
                lease.Resource.ResourceId,
                lease.FencingToken,
                lease.ExpiresAtUtc)).ToArray(),
            completion.Steps.Select(step => new OperationStepExecutionEvidence(
                step.StepId,
                step.NodeId,
                step.ActionId,
                step.TargetKind,
                step.TargetId,
                step.DisplayName,
                step.Status,
                step.StartedAtUtc,
                step.CompletedAtUtc,
                step.FailureReason)).ToArray(),
            completion.Commands.Select(command => new OperationCommandExecutionEvidence(
                command.CommandId,
                command.StepId,
                command.NodeId,
                command.ActionId,
                command.TargetKind,
                command.TargetId,
                command.CapabilityId,
                command.CommandName,
                command.ExecutionStatus,
                command.CreatedAtUtc,
                command.DeadlineAtUtc,
                command.AcceptedAtUtc,
                command.StartedAtUtc,
                command.CompletedAtUtc,
                command.ResultPayload,
                command.FailureReason,
                command.ResultJudgement)).ToArray(),
            completion.Incidents.Select(incident => new OperationIncidentExecutionEvidence(
                incident.IncidentId,
                incident.Severity,
                incident.Code,
                incident.Message,
                incident.OccurredAtUtc)).ToArray(),
            completion.Artifacts.Select(artifact => new OperationArtifactExecutionEvidence(
                artifact.Name,
                artifact.Kind,
                artifact.StorageKey,
                artifact.MediaType,
                artifact.SizeBytes,
                artifact.Sha256)).ToArray());
    }

    public static OperationExecutionEvidence FromCoordinatorFailure(
        StationOperationDispatchRequest request,
        DateTimeOffset completedAtUtc,
        string failureCode,
        string failureReason,
        int incidentCount)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        if (incidentCount is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(incidentCount),
                "Coordinator failure evidence supports its single canonical system Incident.");
        }

        return FromCoordinatorTerminal(
            request,
            completedAtUtc,
            "Failed",
            failureCode,
            failureReason,
            incidentCount);
    }

    public static OperationExecutionEvidence FromCoordinatorCancellation(
        StationOperationDispatchRequest request,
        DateTimeOffset completedAtUtc,
        string cancellationCode,
        string cancellationReason)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancellationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancellationReason);
        return FromCoordinatorTerminal(
            request,
            completedAtUtc,
            "Canceled",
            cancellationCode,
            cancellationReason,
            incidentCount: 0);
    }

    private static OperationExecutionEvidence FromCoordinatorTerminal(
        StationOperationDispatchRequest request,
        DateTimeOffset completedAtUtc,
        string runtimeSessionStatus,
        string failureCode,
        string failureReason,
        int incidentCount)
    {

        var run = request.Run;
        var operation = request.Operation;
        return new OperationExecutionEvidence(
            OperationExecutionEvidenceOrigin.Coordinator,
            request.RuntimeSessionId.Value,
            run.RunId.Value,
            run.ProductionUnitId.Value,
            run.ProductionLineDefinitionId,
            operation.Definition.OperationId,
            operation.OperationRunId,
            operation.Attempt,
            operation.Definition.StationSystemId,
            operation.Definition.StationId.Value,
            operation.Definition.ProcessDefinitionId.Value,
            operation.Definition.ProcessVersionId.Value,
            operation.Definition.ConfigurationSnapshotId.Value,
            operation.Definition.RecipeSnapshotId.Value,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            run.LotId,
            run.CarrierId,
            FindResource(operation, ResourceKind.Fixture),
            FindResource(operation, ResourceKind.Device),
            run.ActorId,
            run.ProjectId,
            run.ApplicationId,
            run.ProjectSnapshotId,
            run.TopologyId,
            runtimeSessionStatus,
            completedAtUtc,
            request.ResourceLeases.Select(lease => new OperationResourceFenceEvidence(
                lease.Resource.Kind.ToString(),
                lease.Resource.ResourceId,
                lease.FencingToken,
                lease.ExpiresAtUtc)).ToArray(),
            [],
            [],
            incidentCount == 0
                ? []
                :
                [
                    new OperationIncidentExecutionEvidence(
                        DeterministicIncidentId(request.IdempotencyKey, failureCode),
                        "Error",
                        failureCode,
                        failureReason,
                        completedAtUtc)
                ],
            []);
    }

    private static Guid DeterministicIncidentId(string idempotencyKey, string failureCode)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{idempotencyKey}/coordinator-failure/{failureCode}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string? FindResource(
        OperationRunSnapshot operation,
        ResourceKind kind) =>
        operation.Definition.ResourceRequirements
            .FirstOrDefault(requirement => requirement.Kind == kind)?.ResourceId;

    private static string ToRuntimeSessionStatus(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Completed => "Completed",
        ExecutionStatus.Canceled => "Canceled",
        ExecutionStatus.Failed or ExecutionStatus.TimedOut or ExecutionStatus.Rejected => "Failed",
        _ => throw new InvalidDataException("Station completion status is not terminal.")
    };
}
