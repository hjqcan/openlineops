using System.Text.Json;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Agent.Contracts;

public sealed record StationJobRequested(
    Guid MessageId,
    Guid JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid ProductionRunId,
    string OperationRunId,
    int OperationAttempt,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string PackageContentSha256,
    string OperationId,
    string FlowDefinitionId,
    string FlowVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    IReadOnlyCollection<StationResourceFence> ResourceFences,
    JsonElement Inputs,
    DateTimeOffset RequestedAtUtc);

public sealed record StationResourceFence(
    string ResourceKind,
    string ResourceId,
    long FencingToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record StationJobAccepted(
    Guid MessageId,
    Guid JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    DateTimeOffset AcceptedAtUtc);

public sealed record StationJobProgressed(
    Guid MessageId,
    Guid JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    int Percent,
    string Phase,
    DateTimeOffset ProgressedAtUtc);

public sealed record StationJobArtifact(
    string RelativePath,
    string MediaType,
    long SizeBytes,
    string Sha256);

public sealed record StationJobCompleted(
    Guid MessageId,
    Guid JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    ExecutionStatus ExecutionStatus,
    ResultJudgement Judgement,
    JsonElement Outputs,
    IReadOnlyCollection<StationJobArtifact> Artifacts,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset CompletedAtUtc);

public sealed record MaterialArrived(
    Guid MessageId,
    string IdempotencyKey,
    string StationId,
    string SlotId,
    string ProductionUnitId,
    string Source,
    DateTimeOffset ArrivedAtUtc);

public sealed record ResourceLeaseChanged(
    Guid MessageId,
    Guid LeaseId,
    string ResourceKind,
    string ResourceId,
    string OwnerId,
    long FencingToken,
    string Status,
    DateTimeOffset ChangedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record EmergencyStopRequested(
    Guid MessageId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    string Reason,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc);

public sealed record EmergencyStopAcknowledged(
    Guid MessageId,
    Guid RequestMessageId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    bool Accepted,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset AcknowledgedAtUtc);

public sealed record StationSafeStopRequested(
    Guid MessageId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid ProductionRunId,
    string? OperationRunId,
    string ActorId,
    string Reason,
    DateTimeOffset RequestedAtUtc);

public sealed record StationSafeStopAcknowledged(
    Guid MessageId,
    Guid RequestMessageId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    bool Accepted,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset AcknowledgedAtUtc);
