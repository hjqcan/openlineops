namespace OpenLineOps.Traceability.Application.Records;

public sealed record CreateTraceRecordRequest(
    Guid ProductionRunId,
    Guid ProductionUnitId,
    string? ProjectId,
    string? ApplicationId,
    string? ProjectSnapshotId,
    string? TopologyId,
    string? ProductionLineDefinitionId,
    string? ProductModelId,
    string? ProductionUnitIdentityInputKey,
    string? ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string? ActorId,
    string? ExecutionStatus,
    string? Judgement,
    string? Disposition,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    IReadOnlyCollection<CreateTraceOperationExecutionRequest>? Operations,
    IReadOnlyCollection<CreateTraceRouteDecisionRequest>? RouteDecisions,
    IReadOnlyCollection<CreateTraceMaterialGenealogyRequest>? Genealogy,
    IReadOnlyCollection<CreateTraceMaterialLocationTransitionRequest>? MaterialLocationTransitions,
    IReadOnlyCollection<CreateTraceSlotOccupancyTransitionRequest>? SlotOccupancyTransitions,
    IReadOnlyCollection<CreateTraceDispositionTransitionRequest>? DispositionTransitions,
    IReadOnlyCollection<CreateAuditEntryRequest>? AuditEntries);

public sealed record CreateTraceMaterialLocationRequest(
    string? Kind,
    string? LineId,
    string? StationSystemId,
    string? SlotId,
    string? CarrierId,
    string? CarrierPositionId);

public sealed record CreateTraceMaterialLocationTransitionRequest(
    Guid EvidenceId,
    Guid? ProductionRunId,
    string? MaterialKind,
    string? MaterialId,
    CreateTraceMaterialLocationRequest? Source,
    CreateTraceMaterialLocationRequest? Destination,
    string? ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record CreateTraceSlotOccupancyTransitionRequest(
    Guid EvidenceId,
    Guid? ProductionRunId,
    string? LineId,
    string? StationSystemId,
    string? SlotId,
    string? MaterialKind,
    string? MaterialId,
    string? PreviousStatus,
    string? CurrentStatus,
    string? ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record CreateTraceDispositionTransitionRequest(
    Guid EvidenceId,
    Guid ProductionUnitId,
    Guid? ProductionRunId,
    string? PreviousDisposition,
    string? CurrentDisposition,
    string? Reason,
    string? ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record CreateTraceMaterialGenealogyRequest(
    Guid LinkId,
    Guid ParentProductionUnitId,
    Guid ChildProductionUnitId,
    string? Relationship,
    string? OperationId,
    string? LinkedBy,
    DateTimeOffset LinkedAtUtc);

public sealed record CreateTraceOperationExecutionRequest(
    string? OperationRunId,
    string? OperationId,
    int Attempt,
    string? StationSystemId,
    string? StationId,
    string? ProcessDefinitionId,
    string? ProcessVersionId,
    string? ConfigurationSnapshotId,
    string? RecipeSnapshotId,
    Guid? RuntimeSessionId,
    string? RuntimeSessionStatus,
    string? ExecutionStatus,
    string? Judgement,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    IReadOnlyCollection<CreateTraceCommandRequest>? Commands,
    IReadOnlyCollection<CreateMeasurementRecordRequest>? Measurements,
    IReadOnlyCollection<CreateArtifactRecordRequest>? Artifacts,
    IReadOnlyCollection<CreateTraceIncidentRequest>? Incidents,
    IReadOnlyCollection<CreateTraceOperationOutputRequest>? Outputs,
    IReadOnlyCollection<CreateTraceResourceFencingTokenRequest>? FencingTokens);

public sealed record CreateTraceRouteDecisionRequest(
    string? SourceOperationRunId,
    string? TransitionId,
    string? TargetOperationId,
    string? TerminalDisposition,
    string? SourceJudgement,
    int Traversal,
    DateTimeOffset DecidedAtUtc);

public sealed record CreateTraceOperationOutputRequest(
    string? Key,
    string? ValueKind,
    string? CanonicalJson);

public sealed record CreateTraceResourceFencingTokenRequest(
    string? ResourceKind,
    string? ResourceId,
    long FencingToken);

public sealed record CreateTraceCommandRequest(
    Guid RuntimeCommandId,
    Guid RuntimeStepId,
    string? ActionId,
    string? TargetKind,
    string? TargetId,
    string? TargetCapabilityId,
    string? CommandName,
    string? ExecutionStatus,
    string? ResultJudgement,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadlineAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultPayload,
    string? FailureReason);

public sealed record CreateMeasurementRecordRequest(
    Guid? MeasurementRecordId,
    string? Name,
    decimal? NumericValue,
    string? TextValue,
    string? Unit,
    string? DeviceId,
    Guid? RuntimeCommandId,
    string? ActionId,
    string? TargetKind,
    string? TargetId,
    string? CommandExecutionStatus,
    string? CommandResultJudgement,
    bool? Passed,
    DateTimeOffset MeasuredAtUtc);

public sealed record CreateArtifactRecordRequest(
    Guid? ArtifactRecordId,
    string? Name,
    string? Kind,
    string? StorageKey,
    string? MediaType,
    long SizeBytes,
    string? Sha256,
    string? DeviceId,
    DateTimeOffset CapturedAtUtc);

public sealed record CreateTraceIncidentRequest(
    Guid RuntimeIncidentId,
    string? Severity,
    string? Code,
    string? Message,
    DateTimeOffset OccurredAtUtc);

public sealed record CreateAuditEntryRequest(
    Guid? AuditEntryId,
    string? ActorId,
    string? Action,
    string? Detail,
    DateTimeOffset OccurredAtUtc);
