namespace OpenLineOps.Traceability.Api.Models;

public sealed record CreateTraceRecordRequest(
    Guid ProductionRunId,
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
    IReadOnlyCollection<CreateAuditEntryRequest>? AuditEntries);

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
    string? Status,
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
    string? CommandStatus,
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
