using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Agent.Application.StationJobs;

public sealed record StationJobPersistenceEntry(StationJobSnapshot Job, long Revision);

public sealed record StationJobOutboxMessage(
    Guid MessageId,
    StationJobId JobId,
    long Sequence,
    string Kind,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount,
    DateTimeOffset? NextAttemptAtUtc,
    DateTimeOffset? AcknowledgedAtUtc);

public interface IStationJobStore
{
    ValueTask<StationJobPersistenceEntry?> GetAsync(
        StationJobId jobId,
        CancellationToken cancellationToken = default);

    ValueTask<StationJobPersistenceEntry?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryAddAsync(
        StationJob job,
        Guid inboundMessageId,
        IReadOnlyCollection<StationJobOutboxMessage> outboxMessages,
        CancellationToken cancellationToken = default);

    ValueTask<long> SaveAsync(
        StationJob job,
        long expectedRevision,
        IReadOnlyCollection<StationJobOutboxMessage> outboxMessages,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<StationJobPersistenceEntry>> ListRecoverableAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<StationJobOutboxMessage>> ListPendingOutboxAsync(
        int maximumCount,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    ValueTask AcknowledgeOutboxAsync(
        Guid messageId,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask RecordOutboxFailureAsync(
        Guid messageId,
        DateTimeOffset retryAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IStationOperationExecutor
{
    ValueTask<StationOperationExecutionResult> ExecuteAsync(
        StationJobSnapshot job,
        Func<StationOperationProgress, CancellationToken, ValueTask> reportProgress,
        CancellationToken cancellationToken = default);
}

public interface IStationResourceFenceValidator
{
    ValueTask<StationResourceFenceValidationResult> ValidateAndAdvanceAsync(
        StationJobSnapshot job,
        CancellationToken cancellationToken = default);
}

public interface IStationRuntimeHost
{
    ValueTask<StationOperationExecutionResult> ExecuteAsync(
        StationRuntimeExecutionRequest request,
        Func<StationOperationProgress, CancellationToken, ValueTask> reportProgress,
        CancellationToken cancellationToken = default);
}

public interface IStationAgentMessagePublisher
{
    ValueTask PublishAsync(
        string kind,
        string payloadJson,
        CancellationToken cancellationToken = default);
}

public interface IStationJobReceiver
{
    Task RunAsync(
        Func<StationJobRequested, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default);
}

public interface IStationSafetyReceiver
{
    Task RunAsync(
        Func<EmergencyStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> emergencyStopHandler,
        Func<StationSafeStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> safeStopHandler,
        CancellationToken cancellationToken = default);
}

public interface IStationSafetyActuator
{
    ValueTask<StationSafetyExecutionResult> EmergencyStopAsync(
        EmergencyStopRequested request,
        CancellationToken cancellationToken = default);

    ValueTask<StationSafetyExecutionResult> SafeStopAsync(
        StationSafeStopRequested request,
        CancellationToken cancellationToken = default);
}

public sealed record StationSafetyExecutionResult(
    bool Accepted,
    string? FailureCode,
    string? FailureReason);

public sealed record StationOperationProgress(int Percent, string Phase);

public sealed record StationResourceFenceValidationResult(bool Accepted, string? RejectionReason)
{
    public static StationResourceFenceValidationResult Accept() => new(true, null);

    public static StationResourceFenceValidationResult Reject(string reason) => new(false, reason);
}

public sealed record StationRuntimeExecutionRequest(
    StationJobSnapshot Job,
    string PackageContentDirectory);

public sealed record StationOperationArtifact(
    string RelativePath,
    string MediaType,
    long SizeBytes,
    string Sha256);

public sealed record StationOperationExecutionResult(
    ExecutionStatus ExecutionStatus,
    ResultJudgement Judgement,
    string OutputsJson,
    IReadOnlyCollection<StationOperationArtifact> Artifacts,
    string? FailureCode,
    string? FailureReason);

public static class StationAgentMessageKinds
{
    public const string JobAccepted = "StationJobAccepted";
    public const string JobProgressed = "StationJobProgressed";
    public const string JobCompleted = "StationJobCompleted";
}
