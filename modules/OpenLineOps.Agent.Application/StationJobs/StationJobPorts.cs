using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
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

public sealed record PendingStationJobArtifact(
    string Name,
    string Kind,
    string LocalArtifactKey,
    string? MediaType,
    long SizeBytes,
    string Sha256);

public sealed record PendingStationJobCompletion(
    StationJobCompleted Completion,
    IReadOnlyCollection<PendingStationJobArtifact> Artifacts);

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

    ValueTask<IReadOnlyCollection<StationJobOutboxMessage>> ListPendingArtifactCleanupAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAcknowledgedOutboxAsync(
        Guid messageId,
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
    ValueTask<StationResourceFenceValidationResult> ValidateCurrentAsync(
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

public interface IStationRuntimeIsolationCleaner
{
    ValueTask CleanupAsync(
        StationJobSnapshot job,
        CancellationToken cancellationToken = default);
}

public sealed class StationRuntimeIsolationCleanupException : Exception
{
    public StationRuntimeIsolationCleanupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IStationAgentMessagePublisher
{
    ValueTask PublishAsync(
        string kind,
        string payloadJson,
        CancellationToken cancellationToken = default);
}

public interface IStationArtifactTransfer
{
    ValueTask<StationJobArtifact> PublishAsync(
        StationJobId jobId,
        PendingStationJobArtifact artifact,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseLocalAsync(
        StationJobId jobId,
        PendingStationJobArtifact artifact,
        CancellationToken cancellationToken = default);
}

public interface IStationJobReceiver
{
    Task RunAsync(
        Func<StationJobRequested, CancellationToken, ValueTask> handler,
        Func<ResourceLeaseChanged, CancellationToken, ValueTask> resourceLeaseHandler,
        CancellationToken cancellationToken = default);
}

public interface IStationResourceLeaseChangeInbox
{
    ValueTask ApplyAsync(
        ResourceLeaseChanged change,
        CancellationToken cancellationToken = default);
}

public sealed class StationResourceLeaseChangeCoordinator(
    string agentId,
    string stationId,
    IStationResourceLeaseChangeInbox inbox)
{
    private readonly string _agentId = Required(agentId, nameof(agentId));
    private readonly string _stationId = Required(stationId, nameof(stationId));
    private readonly IStationResourceLeaseChangeInbox _inbox =
        inbox ?? throw new ArgumentNullException(nameof(inbox));

    public ValueTask HandleAsync(
        ResourceLeaseChanged change,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(change);
        if (!string.Equals(change.AgentId, _agentId, StringComparison.Ordinal)
            || !string.Equals(change.StationId, _stationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Resource lease change does not target this Agent/Station identity.");
        }

        return _inbox.ApplyAsync(change, cancellationToken);
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;
}

public interface IStationSafetyReceiver
{
    Task RunAsync(
        Func<EmergencyStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> emergencyStopHandler,
        Func<StationSafeStopRequested, CancellationToken, ValueTask<StationSafetyExecutionResult>> safeStopHandler,
        Func<StationJobCancelRequested, CancellationToken, ValueTask<StationJobCancelExecutionResult>> jobCancelHandler,
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

public sealed record StationJobCancelExecutionResult(
    bool Accepted,
    string? FailureCode,
    string? FailureReason)
{
    public static StationJobCancelExecutionResult Success() => new(true, null, null);

    public static StationJobCancelExecutionResult Failure(string code, string reason) =>
        new(false, code, reason);
}

public sealed record StationOperationProgress(int Percent, string Phase);

public sealed record StationResourceFenceValidationResult(
    bool Accepted,
    bool Retryable,
    string? RejectionReason)
{
    public static StationResourceFenceValidationResult Accept() => new(true, false, null);

    public static StationResourceFenceValidationResult Retry(string reason) =>
        new(false, true, Required(reason));

    public static StationResourceFenceValidationResult Reject(string reason) =>
        new(false, false, Required(reason));

    private static string Required(string reason) => string.IsNullOrWhiteSpace(reason)
        ? throw new ArgumentException(
            "Resource fence validation reason is required.",
            nameof(reason))
        : reason;
}

public sealed class StationResourceFenceUnavailableException(string message) : Exception(message);

public sealed record StationRuntimeExecutionRequest(
    StationJobSnapshot Job,
    string PackageContentDirectory);

public sealed record StationOperationArtifact(
    string Name,
    string Kind,
    string LocalArtifactKey,
    string? MediaType,
    long SizeBytes,
    string Sha256);

public sealed record StationOperationExecutionResult(
    ExecutionStatus ExecutionStatus,
    ResultJudgement Judgement,
    string OutputsJson,
    IReadOnlyCollection<StationOperationArtifact> Artifacts,
    IReadOnlyCollection<StationJobStepEvidence> Steps,
    IReadOnlyCollection<StationJobCommandEvidence> Commands,
    IReadOnlyCollection<StationJobIncidentEvidence> Incidents,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    string? FailureCode,
    string? FailureReason);

public static class StationAgentMessageKinds
{
    public const string MaterialArrived = nameof(OpenLineOps.Agent.Contracts.MaterialArrived);
    public const string JobAccepted = "StationJobAccepted";
    public const string JobProgressed = "StationJobProgressed";
    public const string JobCompleted = "StationJobCompleted";
    public const string JobRecoveryRequired = "StationJobRecoveryRequired";
    public const string JobCompletionPendingArtifactTransfer =
        "StationJobCompletionPendingArtifactTransfer";
}
