namespace OpenLineOps.Traceability.Application.Records;

public sealed record TraceRecordDetails(
    Guid TraceRecordId,
    Guid ProductionRunId,
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
    IReadOnlyCollection<TraceOperationExecutionDetails> Operations,
    IReadOnlyCollection<TraceRouteDecisionDetails> RouteDecisions,
    IReadOnlyCollection<AuditEntryDetails> AuditEntries);

public sealed record TraceRecordSummary(
    Guid TraceRecordId,
    Guid ProductionRunId,
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
    int RouteDecisionCount);

public sealed record TraceOperationExecutionDetails(
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
    IReadOnlyCollection<TraceCommandDetails> Commands,
    IReadOnlyCollection<MeasurementRecordDetails> Measurements,
    IReadOnlyCollection<ArtifactRecordDetails> Artifacts,
    IReadOnlyCollection<TraceIncidentDetails> Incidents,
    IReadOnlyCollection<TraceOperationOutputDetails> Outputs,
    IReadOnlyCollection<TraceResourceFencingTokenDetails> FencingTokens);

public sealed record TraceRouteDecisionDetails(
    string SourceOperationRunId,
    string TransitionId,
    string TargetOperationId,
    string SourceJudgement,
    int Traversal,
    DateTimeOffset DecidedAtUtc);

public sealed record TraceOperationOutputDetails(string Key, string ValueKind, string CanonicalJson);

public sealed record TraceResourceFencingTokenDetails(
    string ResourceKind,
    string ResourceId,
    long FencingToken);

public sealed record TraceCommandDetails(
    Guid RuntimeCommandId,
    Guid RuntimeStepId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string TargetCapabilityId,
    string CommandName,
    string Status,
    string? ResultJudgement,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadlineAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultPayload,
    string? FailureReason);

public sealed record MeasurementRecordDetails(
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
    string CommandStatus,
    bool? Passed,
    DateTimeOffset MeasuredAtUtc);

public sealed record ArtifactRecordDetails(
    Guid ArtifactRecordId,
    string Name,
    string Kind,
    string StorageKey,
    string? MediaType,
    long SizeBytes,
    string? Sha256,
    string? DeviceId,
    DateTimeOffset CapturedAtUtc);

public sealed record TraceIncidentDetails(
    Guid RuntimeIncidentId,
    string Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc);

public sealed record AuditEntryDetails(
    Guid AuditEntryId,
    string ActorId,
    string Action,
    string? Detail,
    DateTimeOffset OccurredAtUtc);
