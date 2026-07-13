using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Api.RuntimeIntegration;

public sealed class ProductionRunTraceDomainEventSubscriber :
    IRuntimeDomainEventSubscriber,
    IProductionRunTerminalOutboxHandler
{
    private readonly IRuntimeSessionRepository _runtimeSessionRepository;
    private readonly IStationJobCoordinationStore _stationJobs;
    private readonly IProductionMaterialRepository _materials;
    private readonly ITraceRecordService _traceRecordService;

    public ProductionRunTraceDomainEventSubscriber(
        IRuntimeSessionRepository runtimeSessionRepository,
        IStationJobCoordinationStore stationJobs,
        IProductionMaterialRepository materials,
        ITraceRecordService traceRecordService)
    {
        _runtimeSessionRepository = runtimeSessionRepository;
        _stationJobs = stationJobs;
        _materials = materials;
        _traceRecordService = traceRecordService;
    }

    public async ValueTask HandleAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var terminalEvent in domainEvents.OfType<ProductionRunTerminalDomainEvent>())
        {
            await HandleSnapshotAsync(terminalEvent.Run, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask HandleAsync(
        ProductionRunSnapshot run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return HandleSnapshotAsync(run, cancellationToken);
    }

    private async ValueTask HandleSnapshotAsync(
        ProductionRunSnapshot run,
        CancellationToken cancellationToken)
    {
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
            var reconciliation = run.RecoveryDecisions.SingleOrDefault(decision =>
                decision.Kind == ProductionRecoveryDecisionKind.Reconcile
                && string.Equals(
                    decision.OperationRunId,
                    operation.OperationRunId,
                    StringComparison.Ordinal));
            RuntimeSession? session = null;
            StationJobCompleted? stationCompletion = null;
            if (operation.RuntimeSessionId is not null && reconciliation is null)
            {
                session = await _runtimeSessionRepository
                    .GetByIdAsync(operation.RuntimeSessionId.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (session is null)
                {
                    stationCompletion = await _stationJobs
                        .GetCompletionAsync(
                            CreateStationJobIdempotencyKey(run, operation),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            ValidateOperationEvidence(
                run,
                operation,
                session,
                stationCompletion,
                reconciliation);
            operations.Add(ToOperationRequest(
                run,
                operation,
                session,
                stationCompletion,
                reconciliation));
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
        var materialTimeline = (await _materials.ListTimelineAsync(
                ProductionMaterialTimelineQuery.UnionScope(
                    productionUnitId: new ProductionUnitId(run.ProductionUnitId.Value),
                    productionRunId: run.RunId,
                    carrierId: run.CarrierId is null ? null : new CarrierId(run.CarrierId),
                    throughUtc: completedAtUtc),
                cancellationToken)
            .ConfigureAwait(false))
            .DistinctBy(entry => entry.EvidenceId)
            .OrderBy(entry => entry.OccurredAtUtc)
            .ThenBy(entry => entry.EvidenceId)
            .ToArray();
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

        if (result.IsFailure
            && !string.Equals(
                result.Error.Code,
                "Conflict.Traceability.RecordAlreadyExists",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.Error.Message);
        }
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
        RuntimeSession? session,
        StationJobCompleted? stationCompletion,
        ProductionRecoveryDecision? reconciliation)
    {
        var completedAtUtc = operation.CompletedAtUtc
            ?? throw new InvalidOperationException(
                $"Terminal Operation Run {operation.OperationRunId} has no completion timestamp.");
        var deviceId = session?.TraceMetadata.DeviceId
            ?? FindResourceId(operation.Definition, ResourceKind.Device);
        var commands = reconciliation is not null
            ? Array.Empty<CreateTraceCommandRequest>()
            : session is not null
            ? session.Commands.Select(ToCommandRequest).ToArray()
            : stationCompletion?.Commands.Select(ToCommandRequest).ToArray() ?? [];
        var measurements = reconciliation is not null
            ? Array.Empty<CreateMeasurementRecordRequest>()
            : session is not null
            ? session.Commands
                .Select(command => ToMeasurementRequest(command, deviceId, completedAtUtc))
                .ToArray()
            : stationCompletion?.Commands
                .Select(command => ToMeasurementRequest(command, deviceId, completedAtUtc))
                .ToArray() ?? [];
        var incidents = reconciliation is not null
            ? Array.Empty<CreateTraceIncidentRequest>()
            : session is not null
            ? session.Incidents
                .Select(incident => new CreateTraceIncidentRequest(
                    incident.Id.Value,
                    incident.Severity.ToString(),
                    incident.Code,
                    incident.Message,
                    incident.OccurredAtUtc))
                .ToArray()
            : stationCompletion?.Incidents
                .Select(incident => new CreateTraceIncidentRequest(
                    incident.IncidentId,
                    incident.Severity,
                    incident.Code,
                    incident.Message,
                    incident.OccurredAtUtc))
                .ToArray() ?? [];
        var artifacts = reconciliation is not null
            ? Array.Empty<CreateArtifactRecordRequest>()
            : session is not null
            ? session.Commands
                .SelectMany(command => ToArtifactRequests(command, deviceId, completedAtUtc))
                .ToArray()
            : stationCompletion?.Artifacts
                .Select(artifact => ToArtifactRequest(
                    stationCompletion.RuntimeSessionId,
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
            reconciliation is null
                ? session?.Status.ToString() ?? ToRuntimeSessionStatus(stationCompletion)
                : TraceRuntimeSessionStatus.Reconciled.ToString(),
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

    private static CreateTraceCommandRequest ToCommandRequest(RuntimeCommand command)
    {
        return new CreateTraceCommandRequest(
            command.Id.Value,
            command.StepId.Value,
            command.ActionId.Value,
            command.TargetKind,
            command.TargetId,
            command.TargetCapability.Value,
            command.CommandName,
            command.Status.ToString(),
            command.ResultJudgement?.ToString(),
            command.CreatedAtUtc,
            command.DeadlineAtUtc,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason);
    }

    private static CreateTraceCommandRequest ToCommandRequest(StationJobCommandEvidence command)
    {
        return new CreateTraceCommandRequest(
            command.CommandId,
            command.StepId,
            command.ActionId,
            command.TargetKind,
            command.TargetId,
            command.CapabilityId,
            command.CommandName,
            command.Status,
            command.ResultJudgement?.ToString(),
            command.CreatedAtUtc,
            command.DeadlineAtUtc,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason);
    }

    private static CreateMeasurementRecordRequest ToMeasurementRequest(
        RuntimeCommand command,
        string? deviceId,
        DateTimeOffset operationCompletedAtUtc)
    {
        return new CreateMeasurementRecordRequest(
            command.Id.Value,
            command.CommandName,
            null,
            command.ResultPayload ?? command.FailureReason ?? command.Status.ToString(),
            null,
            deviceId,
            command.Id.Value,
            command.ActionId.Value,
            command.TargetKind,
            command.TargetId,
            command.Status.ToString(),
            command.ResultJudgement switch
            {
                ResultJudgement.Passed => true,
                ResultJudgement.Failed => false,
                _ => null
            },
            command.CompletedAtUtc ?? operationCompletedAtUtc);
    }

    private static CreateMeasurementRecordRequest ToMeasurementRequest(
        StationJobCommandEvidence command,
        string? deviceId,
        DateTimeOffset operationCompletedAtUtc)
    {
        return new CreateMeasurementRecordRequest(
            command.CommandId,
            command.CommandName,
            null,
            command.ResultPayload ?? command.FailureReason ?? command.Status,
            null,
            deviceId,
            command.CommandId,
            command.ActionId,
            command.TargetKind,
            command.TargetId,
            command.Status,
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
        StationJobArtifact artifact,
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
        RuntimeCommand command,
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
            CreateArtifactRecordId(command.Id.Value, artifact.StorageKey),
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
        RuntimeSession? session,
        StationJobCompleted? stationCompletion,
        ProductionRecoveryDecision? reconciliation)
    {
        if (!IsTerminal(operation.ExecutionStatus) || operation.CompletedAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Production Run {run.RunId} contains non-terminal Operation Run {operation.OperationRunId}.");
        }

        if (reconciliation is not null)
        {
            Require(
                reconciliation.Kind == ProductionRecoveryDecisionKind.Reconcile
                && string.Equals(
                    reconciliation.OperationRunId,
                    operation.OperationRunId,
                    StringComparison.Ordinal)
                && operation.RuntimeSessionId is not null
                && operation.StartedAtUtc is not null
                && operation.ExecutionStatus == ExecutionStatus.Completed
                && operation.Judgement == reconciliation.ObservedJudgement
                && operation.CompletedAtUtc == reconciliation.DecidedAtUtc
                && operation.CompletedStepCount == 0
                && operation.CommandCount == 0
                && operation.IncidentCount == 0
                && operation.Outputs.Count == reconciliation.ObservedOutputs.Count
                && operation.Outputs.All(output =>
                    reconciliation.ObservedOutputs.TryGetValue(output.Key, out var value)
                    && value == output.Value)
                && session is null
                && stationCompletion is null,
                operation,
                "operator Recovery Decision evidence");
            return;
        }

        if (operation.RuntimeSessionId is null)
        {
            Require(
                session is null
                && stationCompletion is null
                && operation.ExecutionStatus == ExecutionStatus.Canceled
                && operation.StartedAtUtc is null
                && operation.CompletedStepCount == 0
                && operation.CommandCount == 0
                && operation.IncidentCount == 0
                && operation.Outputs.Count == 0
                && operation.FencingTokens.Count == 0,
                operation,
                "pre-dispatch cancellation evidence");
            return;
        }

        if (session is null && stationCompletion is null)
        {
            throw new InvalidOperationException(
                $"Runtime Session {operation.RuntimeSessionId} and deterministic Station completion "
                + $"for Operation Run {operation.OperationRunId} were not found.");
        }


        if (session is null)
        {
            ValidateStationCompletion(run, operation, stationCompletion!);
            return;
        }

        Require(stationCompletion is null, operation, "single Runtime evidence source");

        var metadata = session.TraceMetadata;
        Require(session.IsTerminal && session.CompletedAtUtc is not null, operation, "terminal Runtime Session");
        Require(session.Id == operation.RuntimeSessionId.Value, operation, "Runtime Session id");
        Require(metadata.ProductionRunId == run.RunId, operation, "Production Run id");
        Require(metadata.ProductionUnitId == run.ProductionUnitId, operation, "Production Unit id");
        RequireEqual(
            metadata.ProductionLineDefinitionId,
            run.ProductionLineDefinitionId,
            operation,
            "production line");
        RequireEqual(metadata.OperationId, operation.Definition.OperationId, operation, "Operation id");
        Require(metadata.OperationAttempt == operation.Attempt, operation, "Operation attempt");
        RequireEqual(
            metadata.StationSystemId,
            operation.Definition.StationSystemId,
            operation,
            "Station System");
        RequireEqual(metadata.ProjectId, run.ProjectId, operation, "project");
        RequireEqual(metadata.ApplicationId, run.ApplicationId, operation, "Application");
        RequireEqual(metadata.ProjectSnapshotId, run.ProjectSnapshotId, operation, "project snapshot");
        RequireEqual(metadata.TopologyId, run.TopologyId, operation, "topology");
        RequireEqual(
            metadata.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.ModelId,
            operation,
            "product model");
        RequireEqual(
            metadata.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.InputKey,
            operation,
            "Production Unit identity input");
        RequireEqual(
            metadata.ProductionUnitIdentity.Value,
            run.ProductionUnitIdentity.Value,
            operation,
            "Production Unit identity value");
        RequireEqual(metadata.ActorId, run.ActorId, operation, "actor");
        RequireEqual(metadata.LotId, run.LotId, operation, "lot");
        RequireEqual(metadata.CarrierId, run.CarrierId, operation, "Carrier");
        RequireEqual(
            metadata.FixtureId,
            FindResourceId(operation.Definition, ResourceKind.Fixture),
            operation,
            "fixture resource");
        RequireEqual(
            metadata.DeviceId,
            FindResourceId(operation.Definition, ResourceKind.Device),
            operation,
            "device resource");
        Require(session.StationId == operation.Definition.StationId, operation, "station");
        Require(
            session.ProcessDefinitionId == operation.Definition.ProcessDefinitionId,
            operation,
            "process definition");
        Require(
            session.ProcessVersionId == operation.Definition.ProcessVersionId,
            operation,
            "process version");
        Require(
            session.ConfigurationSnapshotId == operation.Definition.ConfigurationSnapshotId,
            operation,
            "configuration snapshot");
        Require(
            session.RecipeSnapshotId == operation.Definition.RecipeSnapshotId,
            operation,
            "recipe snapshot");
        Require(
            session.Steps.Count(step => step.Status == RuntimeStepStatus.Completed)
                == operation.CompletedStepCount,
            operation,
            "completed step count");
        Require(session.Commands.Count == operation.CommandCount, operation, "command count");
        Require(session.Incidents.Count == operation.IncidentCount, operation, "incident count");
        Require(
            operation.FencingTokens.Count >= operation.Definition.ResourceRequirements.Count
            && operation.FencingTokens.Values.All(static token => token > 0)
            && operation.Definition.ResourceRequirements.All(requirement =>
                operation.FencingTokens.TryGetValue(requirement, out var token) && token > 0),
            operation,
            "resource fencing tokens");
    }

    private static void ValidateStationCompletion(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        StationJobCompleted completion)
    {
        var runtimeSessionId = operation.RuntimeSessionId
            ?? throw new InvalidOperationException(
                $"Operation Run {operation.OperationRunId} has no Runtime Session identity.");
        var idempotencyKey = CreateStationJobIdempotencyKey(run, operation);
        Require(
            completion.JobId == StationJobIdentity.CreateJobId(idempotencyKey),
            operation,
            "Station job id");
        RequireEqual(completion.IdempotencyKey, idempotencyKey, operation, "Station idempotency key");
        Require(completion.RuntimeSessionId == runtimeSessionId.Value, operation, "Runtime Session id");
        RequireEqual(
            completion.StationId,
            operation.Definition.StationId.Value,
            operation,
            "station");
        Require(completion.ExecutionStatus == operation.ExecutionStatus, operation, "execution status");
        Require(completion.Judgement == operation.Judgement, operation, "judgement");
        Require(completion.CompletedAtUtc == operation.CompletedAtUtc, operation, "completion timestamp");
        RequireEqual(completion.FailureCode, operation.FailureCode, operation, "failure code");
        RequireEqual(completion.FailureReason, operation.FailureReason, operation, "failure reason");
        Require(
            completion.CompletedStepCount == operation.CompletedStepCount
            && completion.CompletedStepCount == completion.Steps.Count(step =>
                string.Equals(step.Status, "Completed", StringComparison.Ordinal)),
            operation,
            "completed step count");
        Require(
            completion.CommandCount == operation.CommandCount
            && completion.CommandCount == completion.Commands.Count,
            operation,
            "command count");
        Require(
            completion.IncidentCount == operation.IncidentCount
            && completion.IncidentCount == completion.Incidents.Count,
            operation,
            "incident count");

        var typedOutputs = ProductionContextOutputReader.Read(completion.Outputs);
        Require(
            typedOutputs.Count == operation.Outputs.Count
            && typedOutputs.All(pair => operation.Outputs.TryGetValue(pair.Key, out var value)
                && value == pair.Value),
            operation,
            "typed outputs");

        var stepIds = completion.Steps.Select(step => step.StepId).ToArray();
        Require(
            stepIds.All(id => id != Guid.Empty)
            && stepIds.Distinct().Count() == stepIds.Length
            && completion.Steps.All(step =>
                IsExactTerminalToken<RuntimeStepStatus>(step.Status)
                && Canonical(step.NodeId)
                && Canonical(step.ActionId)
                && Canonical(step.TargetKind)
                && Canonical(step.TargetId)
                && Canonical(step.DisplayName)
                && IsUtc(step.StartedAtUtc)
                && step.CompletedAtUtc is { } completedAtUtc
                && IsUtc(completedAtUtc)
                && completedAtUtc >= step.StartedAtUtc
                && completedAtUtc <= completion.CompletedAtUtc),
            operation,
            "Step identities");
        var stepsById = completion.Steps.ToDictionary(step => step.StepId);
        var commandIds = completion.Commands.Select(command => command.CommandId).ToArray();
        Require(
            commandIds.All(id => id != Guid.Empty)
            && commandIds.Distinct().Count() == commandIds.Length
            && completion.Commands.All(command =>
                stepsById.TryGetValue(command.StepId, out var step)
                && string.Equals(command.NodeId, step.NodeId, StringComparison.Ordinal)
                && string.Equals(command.ActionId, step.ActionId, StringComparison.Ordinal)
                && string.Equals(command.TargetKind, step.TargetKind, StringComparison.Ordinal)
                && string.Equals(command.TargetId, step.TargetId, StringComparison.Ordinal)
                && Canonical(command.CapabilityId)
                && Canonical(command.CommandName)
                && IsExactTerminalToken<OpenLineOps.Runtime.Domain.Commands.RuntimeCommandStatus>(
                    command.Status)
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
                && command.CompletedAtUtc <= completion.CompletedAtUtc),
            operation,
            "Command identities");
        var incidentIds = completion.Incidents.Select(incident => incident.IncidentId).ToArray();
        Require(
            incidentIds.All(id => id != Guid.Empty)
            && incidentIds.Distinct().Count() == incidentIds.Length
            && completion.Incidents.All(incident =>
                IsExactToken<RuntimeIncidentSeverity>(incident.Severity)
                && Canonical(incident.Code)
                && Canonical(incident.Message)
                && IsUtc(incident.OccurredAtUtc)
                && incident.OccurredAtUtc <= completion.CompletedAtUtc),
            operation,
            "Incident identities");
        Require(
            completion.Artifacts.All(artifact =>
                Canonical(artifact.Name)
                && Canonical(artifact.Kind)
                && Canonical(artifact.StorageKey)
                && (artifact.MediaType is null || Canonical(artifact.MediaType))
                && artifact.SizeBytes >= 0
                && artifact.Sha256.Length == 64
                && artifact.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            && completion.Artifacts.Select(artifact => artifact.StorageKey)
                .Distinct(StringComparer.Ordinal).Count() == completion.Artifacts.Count,
            operation,
            "Artifact evidence");
        Require(
            operation.FencingTokens.Count >= operation.Definition.ResourceRequirements.Count
            && operation.FencingTokens.Values.All(static token => token > 0)
            && operation.Definition.ResourceRequirements.All(requirement =>
                operation.FencingTokens.TryGetValue(requirement, out var token) && token > 0),
            operation,
            "resource fencing tokens");
    }

    private static bool IsExactTerminalToken<TEnum>(string value)
        where TEnum : struct, Enum
    {
        return IsExactToken<TEnum>(value)
            && value is not "Pending" and not "Running" and not "Accepted" and not "InProgress";
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

    private static string CreateStationJobIdempotencyKey(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation) =>
        $"{run.RunId.Value:D}/{operation.OperationRunId}";

    private static string? ToRuntimeSessionStatus(StationJobCompleted? completion) =>
        completion?.ExecutionStatus switch
        {
            ExecutionStatus.Completed => RuntimeSessionStatus.Completed.ToString(),
            ExecutionStatus.Canceled => RuntimeSessionStatus.Canceled.ToString(),
            ExecutionStatus.Failed or ExecutionStatus.TimedOut or ExecutionStatus.Rejected =>
                RuntimeSessionStatus.Failed.ToString(),
            null => null,
            _ => throw new InvalidDataException("Station completion status is not terminal.")
        };

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
