using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Application.Abstractions.Time;
using ProductionExecutionStatus = OpenLineOps.Runtime.Contracts.ExecutionStatus;
using ProductionResultJudgement = OpenLineOps.Runtime.Contracts.ResultJudgement;

namespace OpenLineOps.Agent.Application.StationJobs;

public sealed class StationJobCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IStationJobStore _store;
    private readonly IStationOperationExecutor _executor;
    private readonly IStationResourceFenceValidator _resourceFences;
    private readonly IStationSafetyInboxStore _cancellations;
    private readonly StationJobExecutionRegistry _executions;
    private readonly IStationRuntimeIsolationCleaner _runtimeIsolationCleaner;
    private readonly IClock _clock;

    public StationJobCoordinator(
        IStationJobStore store,
        IStationOperationExecutor executor,
        IStationResourceFenceValidator resourceFences,
        IStationSafetyInboxStore cancellations,
        StationJobExecutionRegistry executions,
        IStationRuntimeIsolationCleaner runtimeIsolationCleaner,
        IClock clock)
    {
        _store = store;
        _executor = executor;
        _resourceFences = resourceFences;
        _cancellations = cancellations;
        _executions = executions;
        _runtimeIsolationCleaner = runtimeIsolationCleaner
            ?? throw new ArgumentNullException(nameof(runtimeIsolationCleaner));
        _clock = clock;
    }

    public async ValueTask<StationJobSnapshot> HandleAsync(
        StationJobRequested message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message);

        var existing = await _store
            .GetByIdempotencyKeyAsync(message.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureSameRequest(existing.Job, message);
            return existing.Job;
        }

        var job = StationJob.Request(new StationJobRequest(
            new StationJobId(message.JobId),
            message.IdempotencyKey,
            message.AgentId,
            message.StationId,
            message.StationSystemId,
            message.ProductionRunId,
            message.ProductionUnitId,
            message.RuntimeSessionId,
            new StationOperationRunId(message.OperationRunId),
            message.OperationAttempt,
            message.ProductModelId,
            message.ProductionUnitIdentityInputKey,
            message.ProductionUnitIdentityValue,
            message.LotId,
            message.CarrierId,
            message.ProjectId,
            message.ApplicationId,
            message.ProjectSnapshotId,
            message.ProductionLineDefinitionId,
            message.TopologyId,
            message.ActorId,
            message.PackageContentSha256,
            message.OperationId,
            message.FlowDefinitionId,
            message.FlowVersionId,
            message.ConfigurationSnapshotId,
            message.RecipeSnapshotId,
            message.ResourceFences.Select(fence => new StationResourceFenceEvidence(
                    fence.ResourceKind,
                    fence.ResourceId,
                    fence.FencingToken,
                    fence.ExpiresAtUtc))
                .ToArray(),
            CanonicalJson(message.Inputs),
            message.RequestedAtUtc));
        job.Accept(_clock.UtcNow);
        var acceptedMessage = CreateAccepted(job);
        if (!await _store.TryAddAsync(
                job,
                message.MessageId,
                [acceptedMessage],
                cancellationToken)
            .ConfigureAwait(false))
        {
            var raced = await _store
                .GetByIdempotencyKeyAsync(message.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Station job idempotency race did not persist a job.");
            EnsureSameRequest(raced.Job, message);
            return raced.Job;
        }

        return await ExecuteAcceptedAsync(job, 0, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyCollection<StationJobSnapshot>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await _store.ListRecoverableAsync(cancellationToken).ConfigureAwait(false);
        var recovered = new List<StationJobSnapshot>(entries.Count);
        foreach (var entry in entries)
        {
            var job = StationJob.Restore(entry.Job);
            if (job.Status == StationJobStatus.Accepted)
            {
                recovered.Add(await ExecuteAcceptedAsync(job, entry.Revision, cancellationToken)
                    .ConfigureAwait(false));
                continue;
            }

            if (job.Status == StationJobStatus.Running)
            {
                job.RequireRecovery(
                    "The Agent restarted while a non-idempotent station operation was running. "
                    + "An operator must reconcile the physical station before any retry.");
                await _store.SaveAsync(job, entry.Revision, [], CancellationToken.None)
                    .ConfigureAwait(false);
                _executions.Forget(job.Id);
            }

            if (job.Status == StationJobStatus.RecoveryRequired)
            {
                await _runtimeIsolationCleaner
                    .CleanupAsync(job.ToSnapshot(), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            recovered.Add(job.ToSnapshot());
        }

        return recovered;
    }

    public async ValueTask<StationJobCancelExecutionResult> CancelAsync(
        StationJobCancelRequested request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var entry = await _store.GetAsync(new StationJobId(request.JobId), cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return StationJobCancelExecutionResult.Failure(
                "Agent.StationJobNotFound",
                $"Station job {request.JobId:D} is not persisted on this Agent.");
        }

        EnsureSameCancellationTarget(entry.Job, request);
        if (entry.Job.Status == StationJobStatus.Canceled)
        {
            _executions.Forget(entry.Job.JobId);
            return StationJobCancelExecutionResult.Success();
        }

        if (entry.Job.Status is StationJobStatus.Completed
            or StationJobStatus.Failed
            or StationJobStatus.TimedOut
            or StationJobStatus.Rejected)
        {
            return StationJobCancelExecutionResult.Failure(
                "Agent.StationJobAlreadyTerminal",
                $"Station job {request.JobId:D} already ended as {entry.Job.Status}.");
        }

        if (entry.Job.Status == StationJobStatus.RecoveryRequired)
        {
            return StationJobCancelExecutionResult.Failure(
                "Agent.StationJobRecoveryRequired",
                $"Station job {request.JobId:D} requires physical reconciliation and cannot be replayed or canceled.");
        }

        _executions.RequestCancelOrRemember(entry.Job.JobId);
        var current = await _store.GetAsync(entry.Job.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (current?.Job.Status == StationJobStatus.Canceled)
        {
            _executions.Forget(entry.Job.JobId);
            return StationJobCancelExecutionResult.Success();
        }

        if (current?.Job.Status is StationJobStatus.Completed
            or StationJobStatus.Failed
            or StationJobStatus.TimedOut
            or StationJobStatus.Rejected)
        {
            _executions.Forget(entry.Job.JobId);
            return StationJobCancelExecutionResult.Failure(
                "Agent.StationJobAlreadyTerminal",
                $"Station job {request.JobId:D} ended as {current.Job.Status} while cancellation was in flight.");
        }

        return StationJobCancelExecutionResult.Success();
    }

    private async ValueTask<StationJobSnapshot> ExecuteAcceptedAsync(
        StationJob job,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        using var execution = _executions.Register(job.Id, cancellationToken);
        var persistedCancellation = await _cancellations
            .GetJobCancellationAsync(job.Id, cancellationToken)
            .ConfigureAwait(false);
        if (persistedCancellation is not null || execution.CancellationToken.IsCancellationRequested)
        {
            return await CompleteCanceledAsync(
                    job,
                    expectedRevision,
                    "Station operation was canceled before hardware execution began.")
                .ConfigureAwait(false);
        }

        var fenceValidation = await _resourceFences
            .ValidateAndAdvanceAsync(job.ToSnapshot(), cancellationToken)
            .ConfigureAwait(false);
        if (execution.CancellationToken.IsCancellationRequested)
        {
            return await CompleteCanceledAsync(
                    job,
                    expectedRevision,
                    "Station operation was canceled before hardware execution began.")
                .ConfigureAwait(false);
        }

        if (!fenceValidation.Accepted)
        {
            job.RejectBeforeStart(
                "Agent.ResourceFenceRejected",
                fenceValidation.RejectionReason
                    ?? "Station resource fencing token was rejected.",
                _clock.UtcNow);
            var rejectedMessage = CreateCompleted(job, null, checked(expectedRevision + 1));
            await _store.SaveAsync(
                    job,
                    expectedRevision,
                    [rejectedMessage],
                    CancellationToken.None)
                .ConfigureAwait(false);
            _executions.Forget(job.Id);
            return job.ToSnapshot();
        }

        job.Start(_clock.UtcNow);
        var revision = await _store
            .SaveAsync(job, expectedRevision, [], CancellationToken.None)
            .ConfigureAwait(false);

        async ValueTask ReportProgressAsync(
            StationOperationProgress update,
            CancellationToken progressCancellationToken)
        {
            job.ReportProgress(update.Percent, update.Phase, _clock.UtcNow);
            var progressedMessage = CreateProgressed(job, checked(revision + 1));
            revision = await _store.SaveAsync(
                    job,
                    revision,
                    [progressedMessage],
                    progressCancellationToken)
                .ConfigureAwait(false);
        }

        StationOperationExecutionResult result;
        try
        {
            result = await _executor.ExecuteAsync(
                    job.ToSnapshot(),
                    ReportProgressAsync,
                    execution.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (StationRuntimeIsolationCleanupException exception)
        {
            job.RequireRecovery(
                "Station runtime isolation cleanup failed after execution. "
                + "The Agent must retry cleanup during restart before the Job can be reconciled. "
                + exception.Message);
            await _store.SaveAsync(job, revision, [], CancellationToken.None)
                .ConfigureAwait(false);
            _executions.Forget(job.Id);
            throw;
        }
        catch (OperationCanceledException) when (execution.CancellationToken.IsCancellationRequested)
        {
            result = new StationOperationExecutionResult(
                ProductionExecutionStatus.Canceled,
                ProductionResultJudgement.Aborted,
                "{}",
                [],
                [],
                [],
                [],
                0,
                0,
                0,
                "Agent.ExecutionCanceled",
                "Station operation execution was canceled.");
        }
        catch (Exception exception)
        {
            result = new StationOperationExecutionResult(
                ProductionExecutionStatus.Failed,
                ProductionResultJudgement.Unknown,
                "{}",
                [],
                [],
                [],
                [new StationJobIncidentEvidence(
                    Guid.NewGuid(),
                    "Error",
                    "Agent.ExecutionFailed",
                    exception.Message,
                    _clock.UtcNow)],
                0,
                0,
                1,
                "Agent.ExecutionFailed",
                exception.Message);
        }

        job.Complete(new StationJobCompletion(
            result.ExecutionStatus,
            result.Judgement,
            result.OutputsJson,
            result.CompletedStepCount,
            result.CommandCount,
            result.IncidentCount,
            result.FailureCode,
            result.FailureReason,
            _clock.UtcNow));
        var completedMessage = CreateCompleted(job, result, checked(revision + 1));
        await _store.SaveAsync(job, revision, [completedMessage], CancellationToken.None)
            .ConfigureAwait(false);
        _executions.Forget(job.Id);
        return job.ToSnapshot();
    }

    private async ValueTask<StationJobSnapshot> CompleteCanceledAsync(
        StationJob job,
        long expectedRevision,
        string reason)
    {
        job.Cancel(reason, _clock.UtcNow);
        var completedMessage = CreateCompleted(job, null, checked(expectedRevision + 1));
        await _store.SaveAsync(job, expectedRevision, [completedMessage], CancellationToken.None)
            .ConfigureAwait(false);
        _executions.Forget(job.Id);
        return job.ToSnapshot();
    }

    private StationJobOutboxMessage CreateAccepted(StationJob job)
    {
        var message = new StationJobAccepted(
            Guid.NewGuid(),
            job.Id.Value,
            job.IdempotencyKey,
            job.AgentId,
            job.StationId,
            job.AcceptedAtUtc!.Value);
        return Outbox(message.MessageId, job.Id, 0, StationAgentMessageKinds.JobAccepted, message);
    }

    private StationJobOutboxMessage CreateCompleted(
        StationJob job,
        StationOperationExecutionResult? result,
        long sequence)
    {
        using var outputs = JsonDocument.Parse(job.OutputsJson!);
        var message = new StationJobCompleted(
            Guid.NewGuid(),
            job.Id.Value,
            job.IdempotencyKey,
            job.AgentId,
            job.StationId,
            job.RuntimeSessionId,
            job.ExecutionStatus!.Value,
            job.Judgement!.Value,
            outputs.RootElement.Clone(),
            job.CompletedStepCount,
            job.CommandCount,
            job.IncidentCount,
            result?.Steps ?? [],
            result?.Commands ?? [],
            result?.Incidents ?? [],
            [],
            job.FailureCode,
            job.FailureReason,
            job.CompletedAtUtc!.Value);
        var pendingArtifacts = (result?.Artifacts ?? [])
            .Select(artifact => new PendingStationJobArtifact(
                artifact.Name,
                artifact.Kind,
                artifact.LocalArtifactKey,
                artifact.MediaType,
                artifact.SizeBytes,
                artifact.Sha256))
            .ToArray();
        return pendingArtifacts.Length == 0
            ? Outbox(
                message.MessageId,
                job.Id,
                sequence,
                StationAgentMessageKinds.JobCompleted,
                message)
            : Outbox(
                message.MessageId,
                job.Id,
                sequence,
                StationAgentMessageKinds.JobCompletionPendingArtifactTransfer,
                new PendingStationJobCompletion(message, pendingArtifacts));
    }

    private StationJobOutboxMessage CreateProgressed(StationJob job, long sequence)
    {
        var message = new StationJobProgressed(
            Guid.NewGuid(),
            job.Id.Value,
            job.IdempotencyKey,
            job.AgentId,
            job.StationId,
            job.ProgressPercent,
            job.ProgressPhase!,
            job.LastProgressAtUtc!.Value);
        return Outbox(message.MessageId, job.Id, sequence, StationAgentMessageKinds.JobProgressed, message);
    }

    private StationJobOutboxMessage Outbox<TMessage>(
        Guid messageId,
        StationJobId jobId,
        long sequence,
        string kind,
        TMessage message) => new(
        messageId,
        jobId,
        sequence,
        kind,
        JsonSerializer.Serialize(message, JsonOptions),
        _clock.UtcNow,
        0,
        null,
        null);

    private static void Validate(StationJobRequested message)
    {
        if (message.MessageId == Guid.Empty
            || message.JobId == Guid.Empty
            || message.ProductionRunId == Guid.Empty
            || message.ProductionUnitId == Guid.Empty
            || message.RuntimeSessionId == Guid.Empty)
        {
            throw new ArgumentException("Station job message identities cannot be empty.", nameof(message));
        }

        _ = Required(message.IdempotencyKey, nameof(message.IdempotencyKey));
        _ = Required(message.AgentId, nameof(message.AgentId));
        _ = Required(message.StationId, nameof(message.StationId));
        _ = Required(message.StationSystemId, nameof(message.StationSystemId));
        _ = Required(message.OperationRunId, nameof(message.OperationRunId));
        if (message.OperationAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "Station job operation attempt must be positive.");
        }

        _ = Required(message.ProductModelId, nameof(message.ProductModelId));
        _ = Required(
            message.ProductionUnitIdentityInputKey,
            nameof(message.ProductionUnitIdentityInputKey));
        _ = Required(
            message.ProductionUnitIdentityValue,
            nameof(message.ProductionUnitIdentityValue));
        _ = Required(message.ProjectId, nameof(message.ProjectId));
        _ = Required(message.ApplicationId, nameof(message.ApplicationId));
        _ = Required(message.ProjectSnapshotId, nameof(message.ProjectSnapshotId));
        _ = Required(
            message.ProductionLineDefinitionId,
            nameof(message.ProductionLineDefinitionId));
        _ = Required(message.TopologyId, nameof(message.TopologyId));
        _ = Required(message.ActorId, nameof(message.ActorId));
        _ = Required(message.OperationId, nameof(message.OperationId));
        _ = Required(message.FlowDefinitionId, nameof(message.FlowDefinitionId));
        _ = Required(message.FlowVersionId, nameof(message.FlowVersionId));
        _ = Required(message.ConfigurationSnapshotId, nameof(message.ConfigurationSnapshotId));
        _ = Required(message.RecipeSnapshotId, nameof(message.RecipeSnapshotId));
        ArgumentNullException.ThrowIfNull(message.ResourceFences);
        if (message.PackageContentSha256.Length != 64
            || message.PackageContentSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("Package content SHA-256 is not canonical.", nameof(message));
        }

        if (message.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Station job request timestamp must use UTC offset zero.", nameof(message));
        }
    }

    private static void Validate(StationJobCancelRequested message)
    {
        if (message.MessageId == Guid.Empty
            || message.JobId == Guid.Empty
            || message.ProductionRunId == Guid.Empty)
        {
            throw new ArgumentException(
                "Station job cancellation identities cannot be empty.",
                nameof(message));
        }

        _ = Required(message.IdempotencyKey, nameof(message.IdempotencyKey));
        _ = Required(message.JobIdempotencyKey, nameof(message.JobIdempotencyKey));
        _ = Required(message.AgentId, nameof(message.AgentId));
        _ = Required(message.StationId, nameof(message.StationId));
        _ = Required(message.StationSystemId, nameof(message.StationSystemId));
        _ = Required(message.OperationRunId, nameof(message.OperationRunId));
        _ = Required(message.ActorId, nameof(message.ActorId));
        _ = Required(message.Reason, nameof(message.Reason));
        if (message.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Station job cancellation timestamp must use UTC offset zero.",
                nameof(message));
        }
    }

    private static void EnsureSameCancellationTarget(
        StationJobSnapshot job,
        StationJobCancelRequested request)
    {
        if (job.JobId.Value != request.JobId
            || job.ProductionRunId != request.ProductionRunId
            || !string.Equals(job.IdempotencyKey, request.JobIdempotencyKey, StringComparison.Ordinal)
            || !string.Equals(job.AgentId, request.AgentId, StringComparison.Ordinal)
            || !string.Equals(job.StationId, request.StationId, StringComparison.Ordinal)
            || !string.Equals(job.StationSystemId, request.StationSystemId, StringComparison.Ordinal)
            || !string.Equals(job.OperationRunId.Value, request.OperationRunId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Station job cancellation '{request.IdempotencyKey}' does not match the persisted job identity.");
        }
    }

    private static void EnsureSameRequest(StationJobSnapshot existing, StationJobRequested message)
    {
        if (existing.JobId.Value != message.JobId
            || existing.ProductionRunId != message.ProductionRunId
            || existing.ProductionUnitId != message.ProductionUnitId
            || existing.RuntimeSessionId != message.RuntimeSessionId
            || existing.OperationRunId.Value != message.OperationRunId
            || existing.OperationAttempt != message.OperationAttempt
            || !string.Equals(existing.AgentId, message.AgentId, StringComparison.Ordinal)
            || !string.Equals(existing.StationId, message.StationId, StringComparison.Ordinal)
            || !string.Equals(existing.StationSystemId, message.StationSystemId, StringComparison.Ordinal)
            || !string.Equals(existing.ProductModelId, message.ProductModelId, StringComparison.Ordinal)
            || !string.Equals(
                existing.ProductionUnitIdentityInputKey,
                message.ProductionUnitIdentityInputKey,
                StringComparison.Ordinal)
            || !string.Equals(
                existing.ProductionUnitIdentityValue,
                message.ProductionUnitIdentityValue,
                StringComparison.Ordinal)
            || !string.Equals(existing.LotId, message.LotId, StringComparison.Ordinal)
            || !string.Equals(existing.CarrierId, message.CarrierId, StringComparison.Ordinal)
            || !string.Equals(existing.ProjectId, message.ProjectId, StringComparison.Ordinal)
            || !string.Equals(existing.ApplicationId, message.ApplicationId, StringComparison.Ordinal)
            || !string.Equals(existing.ProjectSnapshotId, message.ProjectSnapshotId, StringComparison.Ordinal)
            || !string.Equals(
                existing.ProductionLineDefinitionId,
                message.ProductionLineDefinitionId,
                StringComparison.Ordinal)
            || !string.Equals(existing.TopologyId, message.TopologyId, StringComparison.Ordinal)
            || !string.Equals(existing.ActorId, message.ActorId, StringComparison.Ordinal)
            || !string.Equals(existing.PackageContentSha256, message.PackageContentSha256, StringComparison.Ordinal)
            || !string.Equals(existing.OperationId, message.OperationId, StringComparison.Ordinal)
            || !string.Equals(existing.FlowDefinitionId, message.FlowDefinitionId, StringComparison.Ordinal)
            || !string.Equals(existing.FlowVersionId, message.FlowVersionId, StringComparison.Ordinal)
            || !string.Equals(existing.ConfigurationSnapshotId, message.ConfigurationSnapshotId, StringComparison.Ordinal)
            || !string.Equals(existing.RecipeSnapshotId, message.RecipeSnapshotId, StringComparison.Ordinal)
            || !SameFences(existing.ResourceFences, message.ResourceFences)
            || !string.Equals(existing.InputsJson, CanonicalJson(message.Inputs), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Idempotency key '{message.IdempotencyKey}' was reused with different station job evidence.");
        }
    }

    private static string CanonicalJson(JsonElement value) => JsonSerializer.Serialize(value);

    private static bool SameFences(
        IReadOnlyList<StationResourceFenceEvidence> existing,
        IReadOnlyCollection<StationResourceFence> requested) =>
        existing.OrderBy(fence => fence.ResourceKind, StringComparer.Ordinal)
            .ThenBy(fence => fence.ResourceId, StringComparer.Ordinal)
            .Select(fence => (
                fence.ResourceKind,
                fence.ResourceId,
                fence.FencingToken,
                fence.ExpiresAtUtc))
            .SequenceEqual(requested.OrderBy(fence => fence.ResourceKind, StringComparer.Ordinal)
                .ThenBy(fence => fence.ResourceId, StringComparer.Ordinal)
                .Select(fence => (
                    fence.ResourceKind,
                    fence.ResourceId,
                    fence.FencingToken,
                    fence.ExpiresAtUtc)));

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException($"{parameterName} must be canonical non-empty text.", parameterName)
            : value;
}
