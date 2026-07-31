namespace OpenLineOps.Traceability.Api.Models;

public sealed record TraceRecordResponse(
    Guid TraceRecordId,
    Guid ProductionRunId,
    Guid ProductionUnitId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string ActorId,
    string ExecutionStatus,
    string Judgement,
    string Disposition,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    IReadOnlyCollection<TraceOperationExecutionResponse> Operations,
    IReadOnlyCollection<TraceRouteDecisionResponse> RouteDecisions,
    IReadOnlyCollection<TraceMaterialGenealogyResponse> Genealogy,
    IReadOnlyCollection<TraceMaterialLocationTransitionResponse> MaterialLocationTransitions,
    IReadOnlyCollection<TraceSlotOccupancyTransitionResponse> SlotOccupancyTransitions,
    IReadOnlyCollection<TraceDispositionTransitionResponse> DispositionTransitions,
    IReadOnlyCollection<AuditEntryResponse> AuditEntries);

public sealed record TraceMaterialLocationResponse(
    string Kind,
    string? LineId,
    string? StationSystemId,
    string? SlotId,
    string? CarrierId,
    string? CarrierPositionId);

public sealed record TraceMaterialLocationTransitionResponse(
    Guid EvidenceId,
    Guid? ProductionRunId,
    string MaterialKind,
    string MaterialId,
    TraceMaterialLocationResponse? Source,
    TraceMaterialLocationResponse Destination,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record TraceSlotOccupancyTransitionResponse(
    Guid EvidenceId,
    Guid? ProductionRunId,
    string LineId,
    string StationSystemId,
    string SlotId,
    string? MaterialKind,
    string? MaterialId,
    string PreviousStatus,
    string CurrentStatus,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record TraceDispositionTransitionResponse(
    Guid EvidenceId,
    Guid ProductionUnitId,
    Guid? ProductionRunId,
    string PreviousDisposition,
    string CurrentDisposition,
    string? Reason,
    string ActorId,
    DateTimeOffset OccurredAtUtc);

public sealed record TraceMaterialGenealogyResponse(
    Guid LinkId,
    Guid ParentProductionUnitId,
    Guid ChildProductionUnitId,
    string Relationship,
    string OperationId,
    string LinkedBy,
    DateTimeOffset LinkedAtUtc);

public sealed record TraceRecordSummaryResponse(
    Guid TraceRecordId,
    Guid ProductionRunId,
    Guid ProductionUnitId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string ActorId,
    string ExecutionStatus,
    string Judgement,
    string Disposition,
    DateTimeOffset CompletedAtUtc,
    int OperationCount,
    int FailedOperationCount,
    int CommandCount,
    int MeasurementCount,
    int ArtifactCount,
    int IncidentCount,
    int RouteDecisionCount,
    int GenealogyCount,
    int MaterialLocationTransitionCount,
    int SlotOccupancyTransitionCount,
    int DispositionTransitionCount);

public sealed record TraceOperationExecutionResponse(
    string OperationRunId,
    string OperationId,
    int Attempt,
    string StationSystemId,
    string StationId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    Guid? RuntimeSessionId,
    string? RuntimeSessionStatus,
    string ExecutionStatus,
    string Judgement,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    IReadOnlyCollection<TraceCommandResponse> Commands,
    IReadOnlyCollection<MeasurementRecordResponse> Measurements,
    IReadOnlyCollection<ArtifactRecordResponse> Artifacts,
    IReadOnlyCollection<TraceIncidentResponse> Incidents,
    IReadOnlyCollection<TraceOperationOutputResponse> Outputs,
    IReadOnlyCollection<TraceResourceFencingTokenResponse> FencingTokens);

public sealed record TraceRouteDecisionResponse(
    string SourceOperationRunId,
    string TransitionId,
    string? TargetOperationId,
    string? TerminalDisposition,
    string SourceJudgement,
    int Traversal,
    DateTimeOffset DecidedAtUtc);

public sealed record TraceOperationOutputResponse(string Key, string ValueKind, string CanonicalJson);

public sealed record TraceResourceFencingTokenResponse(
    string ResourceKind,
    string ResourceId,
    long FencingToken);

public sealed record TraceCommandResponse(
    Guid RuntimeCommandId,
    Guid RuntimeStepId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string TargetCapabilityId,
    string CommandName,
    string ExecutionStatus,
    string ResultJudgement,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadlineAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultPayload,
    string? FailureReason);

public sealed record MeasurementRecordResponse(
    Guid MeasurementRecordId,
    string Name,
    decimal? NumericValue,
    string? TextValue,
    string? Unit,
    string? DeviceId,
    Guid? RuntimeCommandId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string CommandExecutionStatus,
    string CommandResultJudgement,
    bool? Passed,
    DateTimeOffset MeasuredAtUtc);

public sealed record ArtifactRecordResponse(
    Guid ArtifactRecordId,
    string Name,
    string Kind,
    string StorageKey,
    string? MediaType,
    long SizeBytes,
    string? Sha256,
    string? DeviceId,
    DateTimeOffset CapturedAtUtc);

public sealed record TraceIncidentResponse(
    Guid RuntimeIncidentId,
    string Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc);

public sealed record AuditEntryResponse(
    Guid AuditEntryId,
    string ActorId,
    string Action,
    string? Detail,
    DateTimeOffset OccurredAtUtc);
