using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Api.RuntimeIntegration;

public sealed class ProductionRunTraceDomainEventSubscriber : IProductionRunTerminalOutboxHandler
{
    private readonly ITraceRecordService _traceRecordService;

    public ProductionRunTraceDomainEventSubscriber(ITraceRecordService traceRecordService)
    {
        _traceRecordService = traceRecordService
            ?? throw new ArgumentNullException(nameof(traceRecordService));
    }

    public async ValueTask HandleAsync(
        ProductionRunTerminalEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        await ProjectAsync(evidence, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TraceRecordProjectionOutcome> ProjectAsync(
        ProductionRunTerminalEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var run = evidence.Run;
        if (!IsTerminal(run.ExecutionStatus) || run.CompletedAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Production Run terminal event {run.RunId} does not contain terminal state.");
        }

        var operations = new List<CreateTraceOperationExecutionRequest>(run.Operations.Count);
        foreach (var operation in run.Operations
                     .OrderBy(candidate => candidate.StartedAtUtc)
                     .ThenBy(candidate => candidate.OperationRunId, StringComparer.Ordinal))
        {
            var recoveryDecision = operation.RecoveryDecisionId is { } recoveryDecisionId
                ? run.RecoveryDecisions.SingleOrDefault(decision =>
                    decision.DecisionId == recoveryDecisionId)
                : null;
            ValidateOperationEvidence(
                run,
                operation,
                operation.ExecutionEvidence,
                recoveryDecision);
            operations.Add(ToOperationRequest(
                run,
                operation,
                operation.ExecutionEvidence,
                recoveryDecision));
        }

        var routeDecisions = run.RouteDecisions
            .Select(decision => new CreateTraceRouteDecisionRequest(
                decision.SourceOperationRunId,
                decision.TransitionId,
                decision.TargetOperationId,
                decision.TerminalDisposition?.ToString(),
                decision.SourceJudgement.ToString(),
                decision.Traversal,
                decision.DecidedAtUtc))
            .ToArray();
        var completedAtUtc = run.CompletedAtUtc.Value;
        var materialTimeline = evidence.MaterialTimeline;
        var result = await _traceRecordService.CreateAsync(
            new CreateTraceRecordRequest(
                run.RunId.Value,
                run.ProductionUnitId.Value,
                run.ProjectId,
                run.ApplicationId,
                run.ProjectSnapshotId,
                run.TopologyId,
                run.ProductionLineDefinitionId,
                run.ProductionUnitIdentity.ModelId,
                run.ProductionUnitIdentity.InputKey,
                run.ProductionUnitIdentity.Value,
                run.LotId,
                run.CarrierId,
                run.ActorId,
                run.ExecutionStatus.ToString(),
                run.Judgement.ToString(),
                run.Disposition.ToString(),
                run.CreatedAtUtc,
                run.StartedAtUtc,
                completedAtUtc,
                run.FailureCode,
                run.FailureReason,
                operations,
                routeDecisions,
                materialTimeline
                    .Where(entry => entry.Kind == ProductionMaterialEvidenceKind.Genealogy)
                    .Select(ToGenealogyRequest)
                    .ToArray(),
                materialTimeline
                    .Where(entry => entry.Kind == ProductionMaterialEvidenceKind.LocationTransition)
                    .Select(ToLocationTransitionRequest)
                    .ToArray(),
                materialTimeline
                    .Where(entry => entry.Kind == ProductionMaterialEvidenceKind.SlotOccupancyTransition)
                    .Select(ToSlotOccupancyTransitionRequest)
                    .ToArray(),
                materialTimeline
                    .Where(entry => entry.Kind == ProductionMaterialEvidenceKind.DispositionTransition)
                    .Select(ToDispositionTransitionRequest)
                    .ToArray(),
                CreateRunAuditEntries(run, completedAtUtc)),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsFailure)
        {
            return TraceRecordProjectionOutcome.Created;
        }

        if (string.Equals(
                result.Error.Code,
                "Conflict.Traceability.RecordAlreadyExists",
                StringComparison.Ordinal))
        {
            return TraceRecordProjectionOutcome.AlreadyExists;
        }

        throw new InvalidOperationException(result.Error.Message);
    }

    private static CreateTraceMaterialGenealogyRequest ToGenealogyRequest(
        ProductionMaterialTimelineEntry entry)
    {
        var link = entry.Genealogy
            ?? throw new InvalidDataException("Material genealogy timeline entry has no link evidence.");
        return new CreateTraceMaterialGenealogyRequest(
            link.Id.Value,
            link.ParentUnitId.Value,
            link.ChildUnitId.Value,
            link.Relationship,
            link.OperationId,
            link.LinkedBy,
            link.LinkedAtUtc);
    }

    private static CreateTraceMaterialLocationTransitionRequest ToLocationTransitionRequest(
        ProductionMaterialTimelineEntry entry)
    {
        var material = entry.Material
            ?? throw new InvalidDataException("Material location timeline entry has no material.");
        return new CreateTraceMaterialLocationTransitionRequest(
            entry.EvidenceId,
            entry.ProductionRunId?.Value,
            material.Kind.ToString(),
            material.Value,
            entry.SourceLocation is null ? null : ToLocationRequest(entry.SourceLocation),
            ToLocationRequest(entry.DestinationLocation
                ?? throw new InvalidDataException(
                    "Material location timeline entry has no destination.")),
            entry.ActorId,
            entry.OccurredAtUtc);
    }

    private static CreateTraceMaterialLocationRequest ToLocationRequest(MaterialLocation location) =>
        new(
            location.Kind.ToString(),
            location.LineId,
            location.StationSystemId,
            location.SlotId,
            location.CarrierId?.Value,
            location.CarrierPositionId);

    private static CreateTraceSlotOccupancyTransitionRequest ToSlotOccupancyTransitionRequest(
        ProductionMaterialTimelineEntry entry)
    {
        var slot = entry.Slot
            ?? throw new InvalidDataException("Slot occupancy timeline entry has no Slot identity.");
        return new CreateTraceSlotOccupancyTransitionRequest(
            entry.EvidenceId,
            entry.ProductionRunId?.Value,
            slot.LineId,
            slot.StationSystemId,
            slot.SlotId,
            entry.Material?.Kind.ToString(),
            entry.Material?.Value,
            entry.PreviousSlotStatus?.ToString(),
            entry.CurrentSlotStatus?.ToString(),
            entry.ActorId,
            entry.OccurredAtUtc);
    }

    private static CreateTraceDispositionTransitionRequest ToDispositionTransitionRequest(
        ProductionMaterialTimelineEntry entry) => new(
        entry.EvidenceId,
        entry.ProductionUnitId?.Value
            ?? throw new InvalidDataException(
                "Disposition timeline entry has no Production Unit identity."),
        entry.ProductionRunId?.Value,
        entry.PreviousDisposition?.ToString(),
        entry.CurrentDisposition?.ToString(),
        entry.Reason,
        entry.ActorId,
        entry.OccurredAtUtc);

    private static CreateTraceOperationExecutionRequest ToOperationRequest(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        OperationExecutionEvidence? executionEvidence,
        ProductionRecoveryDecision? recoveryDecision)
    {
        var completedAtUtc = operation.CompletedAtUtc
            ?? throw new InvalidOperationException(
                $"Terminal Operation Run {operation.OperationRunId} has no completion timestamp.");
        var deviceId = executionEvidence?.DeviceId
            ?? FindResourceId(operation.Definition, ResourceKind.Device);
        var commands = recoveryDecision is not null
            ? Array.Empty<CreateTraceCommandRequest>()
            : executionEvidence?.Commands.Select(ToCommandRequest).ToArray() ?? [];
        var measurements = recoveryDecision is not null
            ? Array.Empty<CreateMeasurementRecordRequest>()
            : executionEvidence?.Commands
                .Select(command => ToMeasurementRequest(command, deviceId, completedAtUtc))
                .ToArray() ?? [];
        var incidents = recoveryDecision is not null
            ? Array.Empty<CreateTraceIncidentRequest>()
            : executionEvidence?.Incidents
                .Select(incident => new CreateTraceIncidentRequest(
                    incident.IncidentId,
                    incident.Severity,
                    incident.Code,
                    incident.Message,
                    incident.OccurredAtUtc))
                .ToArray() ?? [];
        var artifacts = recoveryDecision is not null
            ? Array.Empty<CreateArtifactRecordRequest>()
            : executionEvidence?.Origin == OperationExecutionEvidenceOrigin.RuntimeSession
            ? executionEvidence.Commands
                .SelectMany(command => ToArtifactRequests(command, deviceId, completedAtUtc))
                .ToArray()
            : executionEvidence?.Artifacts
                .Select(artifact => ToArtifactRequest(
                    executionEvidence.RuntimeSessionId,
                    artifact,
                    deviceId,
                    completedAtUtc))
                .ToArray() ?? [];
        var outputs = operation.Outputs
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new CreateTraceOperationOutputRequest(
                pair.Key,
                pair.Value.Kind.ToString(),
                ToCanonicalJson(pair.Value)))
            .ToArray();
        var fencingTokens = operation.FencingTokens
            .OrderBy(pair => pair.Key.Kind)
            .ThenBy(pair => pair.Key.ResourceId, StringComparer.Ordinal)
            .Select(pair => new CreateTraceResourceFencingTokenRequest(
                pair.Key.Kind.ToString(),
                pair.Key.ResourceId,
                pair.Value))
            .ToArray();

        return new CreateTraceOperationExecutionRequest(
            operation.OperationRunId,
            operation.Definition.OperationId,
            operation.Attempt,
            operation.Definition.StationSystemId,
            operation.Definition.StationId.Value,
            operation.Definition.ProcessDefinitionId.Value,
            operation.Definition.ProcessVersionId.Value,
            operation.Definition.ConfigurationSnapshotId.Value,
            operation.Definition.RecipeSnapshotId.Value,
            operation.RuntimeSessionId?.Value,
            recoveryDecision is null
                ? executionEvidence?.RuntimeSessionStatus
                : recoveryDecision.Kind == ProductionRecoveryDecisionKind.Reconcile
                    ? TraceRuntimeSessionStatus.Reconciled.ToString()
                    : TraceRuntimeSessionStatus.Canceled.ToString(),
            operation.ExecutionStatus.ToString(),
            operation.Judgement.ToString(),
            operation.StartedAtUtc,
            completedAtUtc,
            operation.FailureCode,
            operation.FailureReason,
            operation.CompletedStepCount,
            operation.CommandCount,
            operation.IncidentCount,
            commands,
            measurements,
            artifacts,
            incidents,
            outputs,
            fencingTokens);
    }

    private static CreateTraceCommandRequest ToCommandRequest(
        OperationCommandExecutionEvidence command)
    {
        return new CreateTraceCommandRequest(
            command.CommandId,
            command.StepId,
            command.ActionId,
            command.TargetKind,
            command.TargetId,
            command.CapabilityId,
            command.CommandName,
            command.ExecutionStatus.ToString(),
            command.ResultJudgement!.Value.ToString(),
            command.CreatedAtUtc,
            command.DeadlineAtUtc,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason);
    }

    private static CreateMeasurementRecordRequest ToMeasurementRequest(
        OperationCommandExecutionEvidence command,
        string? deviceId,
        DateTimeOffset operationCompletedAtUtc)
    {
        return new CreateMeasurementRecordRequest(
            command.CommandId,
            command.CommandName,
            null,
            command.ResultPayload ?? command.FailureReason ?? command.ExecutionStatus.ToString(),
            null,
            deviceId,
            command.CommandId,
            command.ActionId,
            command.TargetKind,
            command.TargetId,
            command.ExecutionStatus.ToString(),
            command.ResultJudgement!.Value.ToString(),
            command.ResultJudgement switch
            {
                ResultJudgement.Passed => true,
                ResultJudgement.Failed => false,
                _ => null
            },
            command.CompletedAtUtc ?? operationCompletedAtUtc);
    }

    private static CreateArtifactRecordRequest ToArtifactRequest(
        Guid runtimeSessionId,
        OperationArtifactExecutionEvidence artifact,
        string? deviceId,
        DateTimeOffset capturedAtUtc)
    {
        return new CreateArtifactRecordRequest(
            CreateArtifactRecordId(runtimeSessionId, artifact.StorageKey),
            artifact.Name,
            artifact.Kind,
            artifact.StorageKey,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256,
            deviceId,
            capturedAtUtc);
    }

    private static IEnumerable<CreateArtifactRecordRequest> ToArtifactRequests(
        OperationCommandExecutionEvidence command,
        string? deviceId,
        DateTimeOffset operationCompletedAtUtc)
    {
        var evidence = RuntimeCommandEvidencePayload.Read(command.ResultPayload);
        if (evidence is null)
        {
            return [];
        }

        var capturedAtUtc = command.CompletedAtUtc ?? operationCompletedAtUtc;
        return evidence.Artifacts.Select(artifact => new CreateArtifactRecordRequest(
            CreateArtifactRecordId(command.CommandId, artifact.StorageKey),
            artifact.Name,
            artifact.Kind,
            artifact.StorageKey,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256,
            deviceId,
            capturedAtUtc));
    }

    private static Guid CreateArtifactRecordId(Guid commandId, string storageKey)
    {
        var input = Encoding.UTF8.GetBytes(commandId.ToString("N") + ":" + storageKey);
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Guid CreateRecoveryAuditEntryId(Guid runId, Guid decisionId)
    {
        var input = Encoding.UTF8.GetBytes($"recovery:{runId:N}:{decisionId:N}");
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static List<CreateAuditEntryRequest> CreateRunAuditEntries(
        ProductionRunSnapshot run,
        DateTimeOffset completedAtUtc)
    {
        var entries = run.RecoveryDecisions
            .Select(decision => new CreateAuditEntryRequest(
                CreateRecoveryAuditEntryId(run.RunId.Value, decision.DecisionId),
                decision.ActorId,
                $"ProductionRun.Recovery.{decision.Kind}",
                CreateRecoveryAuditDetail(decision),
                decision.DecidedAtUtc))
            .ToList();
        if (run.SafeStopRequestedAtUtc is { } requestedAtUtc)
        {
            entries.Add(new CreateAuditEntryRequest(
                CreateRunAuditEntryId(run.RunId.Value, "safe-stop-requested"),
                run.SafeStopRequestedBy
                ?? throw new InvalidDataException("Safe Stop Trace evidence has no requesting actor."),
                "ProductionRun.SafeStop.Requested",
                JsonSerializer.Serialize(new
                {
                    Reason = run.SafeStopReason,
                    RequestedAtUtc = requestedAtUtc
                }),
                requestedAtUtc));
        }

        if (run.SafeStopAcknowledgedAtUtc is { } acknowledgedAtUtc)
        {
            entries.Add(new CreateAuditEntryRequest(
                CreateRunAuditEntryId(run.RunId.Value, "safe-stop-acknowledged"),
                "system.station-safety",
                "ProductionRun.SafeStop.Acknowledged",
                JsonSerializer.Serialize(new
                {
                    RequestedAtUtc = run.SafeStopRequestedAtUtc,
                    AcknowledgedAtUtc = acknowledgedAtUtc
                }),
                acknowledgedAtUtc));
        }

        if (run.ScrapRequestedAtUtc is { } scrapRequestedAtUtc)
        {
            var scrapActor = run.ScrapRequestedBy
                ?? throw new InvalidDataException("Scrap Trace evidence has no requesting actor.");
            entries.Add(new CreateAuditEntryRequest(
                CreateRunAuditEntryId(run.RunId.Value, "scrap-requested"),
                scrapActor,
                "ProductionRun.Scrap.Requested",
                JsonSerializer.Serialize(new
                {
                    Reason = run.ScrapReason,
                    RequestedAtUtc = scrapRequestedAtUtc
                }),
                scrapRequestedAtUtc));
            if (run.Disposition == ProductDisposition.Scrapped)
            {
                entries.Add(new CreateAuditEntryRequest(
                    CreateRunAuditEntryId(run.RunId.Value, "scrap-finalized"),
                    scrapActor,
                    "ProductionRun.Scrap.Finalized",
                    JsonSerializer.Serialize(new
                    {
                        RequestedAtUtc = scrapRequestedAtUtc,
                        FinalizedAtUtc = completedAtUtc,
                        OperationRunIds = run.Operations
                            .Where(static operation => operation.StartedAtUtc is not null)
                            .Select(static operation => operation.OperationRunId)
                            .Order(StringComparer.Ordinal)
                            .ToArray()
                    }),
                    completedAtUtc));
            }
        }

        entries.Add(new CreateAuditEntryRequest(
            run.RunId.Value,
            run.ActorId,
            $"ProductionRun.{run.ExecutionStatus}",
            $"Trace record generated from terminal Production Run {run.RunId}.",
            completedAtUtc));
        return entries;
    }

    private static Guid CreateRunAuditEntryId(Guid runId, string evidenceKind)
    {
        var input = Encoding.UTF8.GetBytes($"production-run:{runId:N}:{evidenceKind}");
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string CreateRecoveryAuditDetail(ProductionRecoveryDecision decision) =>
        JsonSerializer.Serialize(new RecoveryAuditDetail(
            decision.DecisionId,
            decision.EvidenceReference,
            decision.Reason,
            decision.OperationRunId,
            decision.OperationId,
            decision.ObservedJudgement?.ToString(),
            decision.ObservedOutputs
                .OrderBy(output => output.Key, StringComparer.Ordinal)
                .Select(output => new RecoveryAuditOutput(
                    output.Key,
                    output.Value.Kind.ToString(),
                    output.Value.CanonicalValue))
                .ToArray()));

    private static string ToCanonicalJson(ProductionContextValue value)
    {
        return value.Kind is ProductionContextValueKind.Text or ProductionContextValueKind.DateTimeUtc
            ? JsonSerializer.Serialize(value.CanonicalValue)
            : value.CanonicalValue;
    }

    private static void ValidateOperationEvidence(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        OperationExecutionEvidence? evidence,
        ProductionRecoveryDecision? recoveryDecision)
    {
        if (!IsTerminal(operation.ExecutionStatus) || operation.CompletedAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Production Run {run.RunId} contains non-terminal Operation Run {operation.OperationRunId}.");
        }

        if (recoveryDecision is not null)
        {
            var validDecision = recoveryDecision.DecisionId == operation.RecoveryDecisionId
                && evidence is null;
            if (recoveryDecision.Kind == ProductionRecoveryDecisionKind.Reconcile)
            {
                validDecision = validDecision
                && string.Equals(
                    recoveryDecision.OperationRunId,
                    operation.OperationRunId,
                    StringComparison.Ordinal)
                && operation.RuntimeSessionId is not null
                && operation.StartedAtUtc is not null
                && operation.ExecutionStatus == ExecutionStatus.Completed
                && operation.Judgement == recoveryDecision.ObservedJudgement
                && operation.CompletedAtUtc == recoveryDecision.DecidedAtUtc
                && operation.CompletedStepCount == 0
                && operation.CommandCount == 0
                && operation.IncidentCount == 0
                && operation.Outputs.Count == recoveryDecision.ObservedOutputs.Count
                && operation.Outputs.All(output =>
                    recoveryDecision.ObservedOutputs.TryGetValue(output.Key, out var value)
                    && value == output.Value);
            }
            else
            {
                validDecision = validDecision
                    && operation.ExecutionStatus == ExecutionStatus.Canceled
                    && operation.Judgement == ResultJudgement.Aborted
                    && operation.CommandCount == 0
                    && operation.IncidentCount == 0
                    && operation.Outputs.Count == 0
                    && (recoveryDecision.Kind != ProductionRecoveryDecisionKind.Retry
                        || string.Equals(
                            operation.Definition.OperationId,
                            recoveryDecision.OperationId,
                            StringComparison.Ordinal));
            }

            Require(validDecision, operation, "operator Recovery Decision evidence");
            return;
        }

        if (evidence is null)
        {
            Require(
                operation.ExecutionStatus == ExecutionStatus.Canceled
                && operation.RecoveryDecisionId is null
                && operation.CommandCount == 0
                && operation.IncidentCount == 0
                && operation.Outputs.Count == 0
                && operation.RuntimeSessionId is null
                && operation.StartedAtUtc is null
                && operation.CompletedStepCount == 0
                && operation.FencingTokens.Count == 0,
                operation,
                "explicit cancellation without execution result");
            return;
        }

        Require(evidence.RuntimeSessionId == operation.RuntimeSessionId?.Value, operation, "Runtime Session id");
        Require(evidence.ProductionRunId == run.RunId.Value, operation, "Production Run id");
        Require(evidence.ProductionUnitId == run.ProductionUnitId.Value, operation, "Production Unit id");
        RequireEqual(evidence.ProductionLineDefinitionId, run.ProductionLineDefinitionId, operation, "production line");
        RequireEqual(evidence.OperationId, operation.Definition.OperationId, operation, "Operation id");
        RequireEqual(evidence.OperationRunId, operation.OperationRunId, operation, "Operation Run id");
        Require(evidence.OperationAttempt == operation.Attempt, operation, "Operation attempt");
        RequireEqual(evidence.StationSystemId, operation.Definition.StationSystemId, operation, "Station System");
        RequireEqual(evidence.StationId, operation.Definition.StationId.Value, operation, "station");
        RequireEqual(evidence.ProcessDefinitionId, operation.Definition.ProcessDefinitionId.Value, operation, "process definition");
        RequireEqual(evidence.ProcessVersionId, operation.Definition.ProcessVersionId.Value, operation, "process version");
        RequireEqual(evidence.ConfigurationSnapshotId, operation.Definition.ConfigurationSnapshotId.Value, operation, "configuration snapshot");
        RequireEqual(evidence.RecipeSnapshotId, operation.Definition.RecipeSnapshotId.Value, operation, "recipe snapshot");
        RequireEqual(evidence.ProjectId, run.ProjectId, operation, "project");
        RequireEqual(evidence.ApplicationId, run.ApplicationId, operation, "Application");
        RequireEqual(evidence.ProjectSnapshotId, run.ProjectSnapshotId, operation, "project snapshot");
        RequireEqual(evidence.TopologyId, run.TopologyId, operation, "topology");
        RequireEqual(evidence.ProductModelId, run.ProductionUnitIdentity.ModelId, operation, "product model");
        RequireEqual(evidence.IdentityInputKey, run.ProductionUnitIdentity.InputKey, operation, "Production Unit identity input");
        RequireEqual(evidence.IdentityValue, run.ProductionUnitIdentity.Value, operation, "Production Unit identity value");
        RequireEqual(evidence.ActorId, run.ActorId, operation, "actor");
        RequireEqual(evidence.LotId, run.LotId, operation, "lot");
        RequireEqual(evidence.CarrierId, run.CarrierId, operation, "Carrier");
        RequireEqual(evidence.FixtureId, FindResourceId(operation.Definition, ResourceKind.Fixture), operation, "fixture resource");
        RequireEqual(evidence.DeviceId, FindResourceId(operation.Definition, ResourceKind.Device), operation, "device resource");
        Require(evidence.CompletedAtUtc == operation.CompletedAtUtc, operation, "completion timestamp");
        Require(
            evidence.Steps.Count(step => string.Equals(step.Status, "Completed", StringComparison.Ordinal))
                == operation.CompletedStepCount,
            operation,
            "completed step count");
        Require(evidence.Commands.Count == operation.CommandCount, operation, "command count");
        Require(evidence.Incidents.Count == operation.IncidentCount, operation, "incident count");
        Require(
            operation.FencingTokens.Count >= operation.Definition.ResourceRequirements.Count
            && operation.FencingTokens.Values.All(static token => token > 0)
            && operation.Definition.ResourceRequirements.All(requirement =>
                operation.FencingTokens.TryGetValue(requirement, out var token)
                && evidence.ResourceFences.Any(fence =>
                    string.Equals(fence.ResourceKind, requirement.Kind.ToString(), StringComparison.Ordinal)
                    && string.Equals(fence.ResourceId, requirement.ResourceId, StringComparison.Ordinal)
                    && fence.FencingToken == token))
            && evidence.ResourceFences.Count == operation.FencingTokens.Count,
            operation,
            "resource fencing tokens");
        var stepIds = evidence.Steps.Select(step => step.StepId).ToArray();
        Require(
            stepIds.All(id => id != Guid.Empty)
            && stepIds.Distinct().Count() == stepIds.Length
            && evidence.Steps.All(step =>
                IsExactTerminalToken<OpenLineOps.Runtime.Domain.Steps.RuntimeStepStatus>(step.Status)
                && Canonical(step.NodeId)
                && Canonical(step.ActionId)
                && Canonical(step.TargetKind)
                && Canonical(step.TargetId)
                && Canonical(step.DisplayName)
                && IsUtc(step.StartedAtUtc)
                && step.CompletedAtUtc is { } completedAtUtc
                && IsUtc(completedAtUtc)
                && completedAtUtc >= step.StartedAtUtc
                && completedAtUtc <= evidence.CompletedAtUtc),
            operation,
            "Step identities");
        var stepsById = evidence.Steps.ToDictionary(step => step.StepId);
        var commandIds = evidence.Commands.Select(command => command.CommandId).ToArray();
        Require(
            commandIds.All(id => id != Guid.Empty)
            && commandIds.Distinct().Count() == commandIds.Length
            && evidence.Commands.All(command =>
                stepsById.TryGetValue(command.StepId, out var step)
                && string.Equals(command.NodeId, step.NodeId, StringComparison.Ordinal)
                && string.Equals(command.ActionId, step.ActionId, StringComparison.Ordinal)
                && string.Equals(command.TargetKind, step.TargetKind, StringComparison.Ordinal)
                && string.Equals(command.TargetId, step.TargetId, StringComparison.Ordinal)
                && Canonical(command.CapabilityId)
                && Canonical(command.CommandName)
                && command.ExecutionStatus is not ExecutionStatus.Pending
                    and not ExecutionStatus.Running
                && Enum.IsDefined(command.ExecutionStatus)
                && IsUtc(command.CreatedAtUtc)
                && IsUtc(command.DeadlineAtUtc)
                && command.DeadlineAtUtc >= command.CreatedAtUtc
                && OptionalUtc(command.AcceptedAtUtc)
                && OptionalUtc(command.StartedAtUtc)
                && OptionalUtc(command.CompletedAtUtc)
                && command.AcceptedAtUtc >= command.CreatedAtUtc
                && command.StartedAtUtc >= (command.AcceptedAtUtc ?? command.CreatedAtUtc)
                && command.CompletedAtUtc >= (command.StartedAtUtc
                    ?? command.AcceptedAtUtc
                    ?? command.CreatedAtUtc)
                && command.CompletedAtUtc <= evidence.CompletedAtUtc),
            operation,
            "Command identities");
        var incidentIds = evidence.Incidents.Select(incident => incident.IncidentId).ToArray();
        Require(
            incidentIds.All(id => id != Guid.Empty)
            && incidentIds.Distinct().Count() == incidentIds.Length
            && evidence.Incidents.All(incident =>
                IsExactToken<RuntimeIncidentSeverity>(incident.Severity)
                && Canonical(incident.Code)
                && Canonical(incident.Message)
                && IsUtc(incident.OccurredAtUtc)
                && incident.OccurredAtUtc <= evidence.CompletedAtUtc),
            operation,
            "Incident identities");
        Require(
            evidence.Artifacts.All(artifact =>
                Canonical(artifact.Name)
                && Canonical(artifact.Kind)
                && Canonical(artifact.StorageKey)
                && (artifact.MediaType is null || Canonical(artifact.MediaType))
                && artifact.SizeBytes >= 0
                && artifact.Sha256.Length == 64
                && artifact.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            && evidence.Artifacts.Select(artifact => artifact.StorageKey)
                .Distinct(StringComparer.Ordinal).Count() == evidence.Artifacts.Count,
            operation,
            "Artifact evidence");
    }

    private static bool IsExactTerminalToken<TEnum>(string value)
        where TEnum : struct, Enum
    {
        return IsExactToken<TEnum>(value)
            && value is not "Pending" and not "Running";
    }

    private static bool IsExactToken<TEnum>(string value)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(parsed.ToString(), value, StringComparison.Ordinal);

    private static bool Canonical(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1]);

    private static bool IsUtc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;

    private static bool OptionalUtc(DateTimeOffset? value) =>
        value is null || IsUtc(value.Value);

    private static string? FindResourceId(OperationRunDefinition definition, ResourceKind kind) =>
        definition.ResourceRequirements.FirstOrDefault(requirement => requirement.Kind == kind)?.ResourceId;

    private static bool IsTerminal(ExecutionStatus status) => status is
        ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Canceled
        or ExecutionStatus.Rejected;

    private static void Require(bool condition, OperationRunSnapshot operation, string field)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"Operation Run {operation.OperationRunId} Runtime evidence differs in {field}.");
        }
    }

    private static void RequireEqual(
        string? actual,
        string? expected,
        OperationRunSnapshot operation,
        string field)
    {
        Require(string.Equals(actual, expected, StringComparison.Ordinal), operation, field);
    }

    private sealed record RecoveryAuditDetail(
        Guid DecisionId,
        string EvidenceReference,
        string Reason,
        string? OperationRunId,
        string? OperationId,
        string? ObservedJudgement,
        IReadOnlyCollection<RecoveryAuditOutput> ObservedOutputs);

    private sealed record RecoveryAuditOutput(
        string Key,
        string Kind,
        string CanonicalValue);
}

public enum TraceRecordProjectionOutcome
{
    Created,
    AlreadyExists
}
