using System.IO.Pipes;
using System.Text.Json;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Execution;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.StationRuntime.Contracts;

namespace OpenLineOps.Agent.Tests;

public sealed class StationJobCoordinatorTests
{
    private static readonly JsonSerializerOptions MessageJsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunningStationAuthorityRejectsJobImmediatelyAfterFenceReplacement()
    {
        var validator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        var first = CreateAcceptedJob(42).ToSnapshot();
        await ApplyLeaseAsync(validator, first);
        Assert.True((await validator.ValidateCurrentAsync(first)).Accepted);
        var authority = new StationResourceFenceAuthorityServer(first, validator);
        using var cancellation = new CancellationTokenSource();
        var server = authority.RunAsync(cancellation.Token);

        try
        {
            Assert.True((await ValidateViaAuthorityAsync(authority, first)).Accepted);
            var replacement = CreateAcceptedJob(43).ToSnapshot();
            await ApplyLeaseAsync(validator, replacement);
            Assert.True((await validator.ValidateCurrentAsync(replacement)).Accepted);

            var stale = await ValidateViaAuthorityAsync(authority, first);

            Assert.False(stale.Accepted);
            Assert.Contains("does not exactly match", stale.RejectionReason, StringComparison.Ordinal);
        }
        finally
        {
            await cancellation.CancelAsync();
            await server;
        }
    }

    [Fact]
    public async Task HandlePersistsInboxProgressAndCompletedResultBeforePublish()
    {
        var store = new InMemoryStationJobStore();
        var request = CreateRequest();
        var validator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        await ApplyLeaseAsync(validator, request);
        var stepId = Guid.NewGuid();
        var executor = new RecordingExecutor(new StationOperationExecutionResult(
            ExecutionStatus.Completed,
            ResultJudgement.Failed,
            "{\"measuredVoltage\":0.4}",
            [new StationOperationArtifact(
                "result.json",
                "VendorReport",
                "reports/result.json",
                "application/json",
                23,
                new string('a', 64))],
            [new StationJobStepEvidence(
                stepId,
                "node.measure",
                "action.measure",
                "System",
                "system.tester",
                "Measure voltage",
                "Completed",
                Now.AddSeconds(-1),
                Now,
                null)],
            [new StationJobCommandEvidence(
                Guid.NewGuid(),
                stepId,
                "node.measure",
                "action.measure",
                "System",
                "system.tester",
                "device.tester",
                "Measure",
                ExecutionStatus.Completed,
                Now.AddSeconds(-1),
                Now.AddMinutes(1),
                Now.AddSeconds(-1),
                Now.AddSeconds(-1),
                Now,
                "{\"measuredVoltage\":{\"kind\":\"FixedPoint\",\"value\":\"0.4\"}}",
                null,
                ResultJudgement.Failed)],
            [new StationJobIncidentEvidence(
                Guid.NewGuid(),
                "Warning",
                "Vendor.Nonconforming",
                "Product result was outside tolerance.",
                Now)],
            1,
            1,
            1,
            null,
            null));
        var coordinator = new StationJobCoordinator(
            store,
            executor,
            validator,
            new EmptyCancellationStore(),
            new StationJobExecutionRegistry(),
            new RecordingIsolationCleaner(),
            new FixedClock(Now));
        var result = await coordinator.HandleAsync(request);
        var duplicate = await coordinator.HandleAsync(request);
        var outbox = new List<StationJobOutboxMessage>();
        for (var sequence = 0; sequence < 3; sequence++)
        {
            var pending = Assert.Single(await store.ListPendingOutboxAsync(10, Now));
            outbox.Add(pending);
            await store.AcknowledgeOutboxAsync(pending.MessageId, Now);
        }

        Assert.Equal(StationJobStatus.Completed, result.Status);
        Assert.Equal(ExecutionStatus.Completed, result.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, result.Judgement);
        Assert.Equal(1, result.CompletedStepCount);
        Assert.Equal(1, result.CommandCount);
        Assert.Equal(1, result.IncidentCount);
        Assert.Equal(result.JobId, duplicate.JobId);
        Assert.Equal(result.Status, duplicate.Status);
        Assert.Equal(result.ExecutionStatus, duplicate.ExecutionStatus);
        Assert.Equal(result.Judgement, duplicate.Judgement);
        Assert.Equal(result.ResourceFences, duplicate.ResourceFences);
        Assert.Equal(1, executor.ExecutionCount);
        Assert.Equal(
            [
                StationAgentMessageKinds.JobAccepted,
                StationAgentMessageKinds.JobProgressed,
                StationAgentMessageKinds.JobCompletionPendingArtifactTransfer
            ],
            outbox.Select(message => message.Kind));
    }

    [Fact]
    public async Task HandleRejectsReusedIdempotencyKeyWithDifferentEvidence()
    {
        var request = CreateRequest();
        var validator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        await ApplyLeaseAsync(validator, request);
        var coordinator = new StationJobCoordinator(
            new InMemoryStationJobStore(),
            new RecordingExecutor(Success()),
            validator,
            new EmptyCancellationStore(),
            new StationJobExecutionRegistry(),
            new RecordingIsolationCleaner(),
            new FixedClock(Now));
        await coordinator.HandleAsync(request);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.HandleAsync(request with { OperationId = "operation-inspection" }));

        Assert.Contains("reused with different", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleResourceFenceIsRejectedBeforeStationRuntimeExecutes()
    {
        var executor = new RecordingExecutor(Success());
        var first = CreateRequest();
        var validator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        await ApplyLeaseAsync(validator, first);
        var coordinator = new StationJobCoordinator(
            new InMemoryStationJobStore(),
            executor,
            validator,
            new EmptyCancellationStore(),
            new StationJobExecutionRegistry(),
            new RecordingIsolationCleaner(),
            new FixedClock(Now));
        var accepted = await coordinator.HandleAsync(first);
        var stale = first with
        {
            MessageId = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            IdempotencyKey = "run/operation/attempt-2",
            OperationRunId = "operation-assemble@2",
            OperationAttempt = 2,
            ResourceFences = [new StationResourceFence(
                "Station",
                "system-station-assembly",
                41,
                Now.AddHours(1))]
        };

        var rejected = await coordinator.HandleAsync(stale);

        Assert.Equal(StationJobStatus.Completed, accepted.Status);
        Assert.Equal(StationJobStatus.Rejected, rejected.Status);
        Assert.Equal(ExecutionStatus.Rejected, rejected.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, rejected.Judgement);
        Assert.Equal("Agent.ResourceFenceRejected", rejected.FailureCode);
        Assert.Equal(1, executor.ExecutionCount);
    }

    [Fact]
    public async Task OfflineDelayedJobWithExpiredFenceNeverExecutesHardware()
    {
        var delayedNow = Now.AddMinutes(2);
        var clock = new FixedClock(delayedNow);
        var executor = new RecordingExecutor(Success());
        var coordinator = new StationJobCoordinator(
            new InMemoryStationJobStore(),
            executor,
            new InMemoryStationResourceFenceValidator(clock),
            new EmptyCancellationStore(),
            new StationJobExecutionRegistry(),
            new RecordingIsolationCleaner(),
            clock);
        var request = CreateRequest() with
        {
            ResourceFences = [new StationResourceFence(
                "Station",
                "system-station-assembly",
                42,
                Now.AddMinutes(1))]
        };

        var rejected = await coordinator.HandleAsync(request);

        Assert.Equal(StationJobStatus.Rejected, rejected.Status);
        Assert.Equal(ExecutionStatus.Rejected, rejected.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, rejected.Judgement);
        Assert.Equal("Agent.ResourceFenceRejected", rejected.FailureCode);
        Assert.Equal(0, executor.ExecutionCount);
        Assert.Null(rejected.StartedAtUtc);
    }

    [Fact]
    public async Task AcceptedJobWaitsForDurableLeaseGrantAndExecutesExactlyOnceAfterRedelivery()
    {
        var request = CreateRequest();
        var store = new InMemoryStationJobStore();
        var executor = new RecordingExecutor(Success());
        var validator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        var coordinator = new StationJobCoordinator(
            store,
            executor,
            validator,
            new EmptyCancellationStore(),
            new StationJobExecutionRegistry(),
            new RecordingIsolationCleaner(),
            new FixedClock(Now));

        await Assert.ThrowsAsync<StationResourceFenceUnavailableException>(async () =>
            await coordinator.HandleAsync(request));
        Assert.Equal(0, executor.ExecutionCount);
        Assert.Equal(
            StationJobStatus.Accepted,
            (await store.GetAsync(new StationJobId(request.JobId)))!.Job.Status);

        await ApplyLeaseAsync(validator, request);
        var completed = await coordinator.HandleAsync(request);
        var duplicate = await coordinator.HandleAsync(request);

        Assert.Equal(StationJobStatus.Completed, completed.Status);
        Assert.Equal(completed.JobId, duplicate.JobId);
        Assert.Equal(completed.Status, duplicate.Status);
        Assert.Equal(completed.ExecutionStatus, duplicate.ExecutionStatus);
        Assert.Equal(completed.Judgement, duplicate.Judgement);
        Assert.Equal(completed.ResourceFences, duplicate.ResourceFences);
        Assert.Equal(1, executor.ExecutionCount);
    }

    [Fact]
    public async Task MissingLeaseBecomesPermanentRejectionAtFenceExpiryWithoutHardwareExecution()
    {
        var request = CreateRequest() with
        {
            ResourceFences = [new StationResourceFence(
                "Station",
                "system-station-assembly",
                42,
                Now.AddSeconds(1))]
        };
        var clock = new MutableClock(Now);
        var executor = new RecordingExecutor(Success());
        var coordinator = new StationJobCoordinator(
            new InMemoryStationJobStore(),
            executor,
            new InMemoryStationResourceFenceValidator(clock),
            new EmptyCancellationStore(),
            new StationJobExecutionRegistry(),
            new RecordingIsolationCleaner(),
            clock);

        await Assert.ThrowsAsync<StationResourceFenceUnavailableException>(async () =>
            await coordinator.HandleAsync(request));
        clock.UtcNow = Now.AddSeconds(1);

        var rejected = await coordinator.HandleAsync(request);

        Assert.Equal(StationJobStatus.Rejected, rejected.Status);
        Assert.Equal("Agent.ResourceFenceRejected", rejected.FailureCode);
        Assert.Equal(0, executor.ExecutionCount);
    }

    [Fact]
    public async Task RecoverMarksRunningHardwareWorkAsRecoveryRequiredWithoutReplay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openlineops-agent-{Guid.NewGuid():N}.db");
        try
        {
            await using var fixture = await RunningSqliteJob.CreateAsync(path);
            var executor = new RecordingExecutor(Success());
            var isolationCleaner = new RecordingIsolationCleaner();
            var coordinator = new StationJobCoordinator(
                fixture.Store,
                executor,
                new SqliteStationResourceFenceValidator(
                    $"Data Source={path}",
                    new FixedClock(Now)),
                new EmptyCancellationStore(),
                new StationJobExecutionRegistry(),
                isolationCleaner,
                new FixedClock(Now));

            var recovered = await coordinator.RecoverAsync();
            var persisted = await fixture.Store.GetAsync(fixture.Job.Id);

            var item = Assert.Single(recovered);
            Assert.Equal(StationJobStatus.RecoveryRequired, item.Status);
            Assert.Equal(ExecutionStatus.Failed, item.ExecutionStatus);
            Assert.Equal(ResultJudgement.Unknown, item.Judgement);
            Assert.Equal(0, executor.ExecutionCount);
            Assert.Equal(StationJobStatus.RecoveryRequired, persisted!.Job.Status);
            Assert.Equal(fixture.Job.Id, Assert.Single(isolationCleaner.CleanedJobs).JobId);
            var recoveryOutbox = Assert.Single(
                await fixture.Store.ListPendingOutboxAsync(10, Now));
            Assert.Equal(StationAgentMessageKinds.JobRecoveryRequired, recoveryOutbox.Kind);
            var recoveryMessage = JsonSerializer.Deserialize<StationJobRecoveryRequired>(
                recoveryOutbox.PayloadJson,
                MessageJsonOptions);
            Assert.NotNull(recoveryMessage);
            Assert.Equal(fixture.Job.Id.Value, recoveryMessage.JobId);
            Assert.Equal(fixture.Job.ProductionRunId, recoveryMessage.ProductionRunId);
            Assert.Equal(fixture.Job.OperationRunId.Value, recoveryMessage.OperationRunId);
            Assert.Equal(fixture.Job.RuntimeSessionId, recoveryMessage.RuntimeSessionId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteSqliteFiles(path);
        }
    }

    [Fact]
    public async Task IsolationCleanupFailureRemainsRecoverableForStartupRetry()
    {
        var store = new InMemoryStationJobStore();
        var cleaner = new RecordingIsolationCleaner();
        var request = CreateRequest();
        var validator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        await ApplyLeaseAsync(validator, request);
        var coordinator = new StationJobCoordinator(
            store,
            new CleanupFailureExecutor(),
            validator,
            new EmptyCancellationStore(),
            new StationJobExecutionRegistry(),
            cleaner,
            new FixedClock(Now));
        await Assert.ThrowsAsync<StationRuntimeIsolationCleanupException>(async () =>
            await coordinator.HandleAsync(request));
        var persisted = await store.GetAsync(new StationJobId(request.JobId));
        Assert.Equal(StationJobStatus.RecoveryRequired, persisted!.Job.Status);

        var recovered = await coordinator.RecoverAsync();

        Assert.Equal(StationJobStatus.RecoveryRequired, Assert.Single(recovered).Status);
        Assert.Equal(new StationJobId(request.JobId), Assert.Single(cleaner.CleanedJobs).JobId);
    }

    [Fact]
    public async Task RestartWithAcceptedJobAndMissingLeaseKeepsAgentAvailableForBrokerRedelivery()
    {
        var request = CreateRequest();
        var store = new InMemoryStationJobStore();
        var validator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
        var executor = new RecordingExecutor(Success());
        var first = new StationJobCoordinator(
            store,
            executor,
            validator,
            new EmptyCancellationStore(),
            new StationJobExecutionRegistry(),
            new RecordingIsolationCleaner(),
            new FixedClock(Now));
        await Assert.ThrowsAsync<StationResourceFenceUnavailableException>(async () =>
            await first.HandleAsync(request));

        var restarted = new StationJobCoordinator(
            store,
            executor,
            validator,
            new EmptyCancellationStore(),
            new StationJobExecutionRegistry(),
            new RecordingIsolationCleaner(),
            new FixedClock(Now));
        var recovered = await restarted.RecoverAsync();

        Assert.Equal(StationJobStatus.Accepted, Assert.Single(recovered).Status);
        Assert.Equal(0, executor.ExecutionCount);
        await ApplyLeaseAsync(validator, request);
        Assert.Equal(StationJobStatus.Completed, (await restarted.HandleAsync(request)).Status);
        Assert.Equal(1, executor.ExecutionCount);
    }

    [Fact]
    public async Task DurableCancellationBeforeDispatchPreventsHardwareExecution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openlineops-agent-cancel-{Guid.NewGuid():N}.db");
        try
        {
            using var cancellations = new SqliteStationSafetyInboxStore($"Data Source={path}");
            var request = CreateRequest();
            var cancel = CreateCancellation(request);
            var control = new StationSafetyCommandCoordinator(cancellations, new FixedClock(Now));
            var acknowledgement = await control.HandleJobCancelAsync(
                cancel,
                (_, _) => ValueTask.FromResult(StationJobCancelExecutionResult.Success()));
            var executor = new RecordingExecutor(Success());
            var coordinator = new StationJobCoordinator(
                new InMemoryStationJobStore(),
                executor,
                new InMemoryStationResourceFenceValidator(new FixedClock(Now)),
                cancellations,
                new StationJobExecutionRegistry(),
                new RecordingIsolationCleaner(),
                new FixedClock(Now));

            var result = await coordinator.HandleAsync(request);

            Assert.True(acknowledgement.Accepted);
            Assert.Equal(StationJobStatus.Canceled, result.Status);
            Assert.Equal(ExecutionStatus.Canceled, result.ExecutionStatus);
            Assert.Equal(ResultJudgement.Aborted, result.Judgement);
            Assert.Equal(0, executor.ExecutionCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteSqliteFiles(path);
        }
    }

    [Fact]
    public async Task ActiveJobCancellationReachesExecutorTokenAndPersistsCanceledResult()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openlineops-agent-cancel-{Guid.NewGuid():N}.db");
        try
        {
            using var cancellations = new SqliteStationSafetyInboxStore($"Data Source={path}");
            var store = new InMemoryStationJobStore();
            var executor = new CancelableExecutor();
            var executions = new StationJobExecutionRegistry();
            var request = CreateRequest();
            var validator = new InMemoryStationResourceFenceValidator(new FixedClock(Now));
            await ApplyLeaseAsync(validator, request);
            var coordinator = new StationJobCoordinator(
                store,
                executor,
                validator,
                cancellations,
                executions,
                new RecordingIsolationCleaner(),
                new FixedClock(Now));
            var execution = coordinator.HandleAsync(request).AsTask();
            await executor.Started.WaitAsync(TimeSpan.FromSeconds(5));
            var control = new StationSafetyCommandCoordinator(cancellations, new FixedClock(Now));

            var acknowledgement = await control.HandleJobCancelAsync(
                CreateCancellation(request),
                coordinator.CancelAsync);
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
            var accepted = Assert.Single(await store.ListPendingOutboxAsync(10, Now));
            Assert.Equal(StationAgentMessageKinds.JobAccepted, accepted.Kind);
            await store.AcknowledgeOutboxAsync(accepted.MessageId, Now);
            var outbox = await store.ListPendingOutboxAsync(10, Now);

            Assert.True(acknowledgement.Accepted);
            Assert.True(executor.ExecutionToken.IsCancellationRequested);
            Assert.Equal(StationJobStatus.Canceled, result.Status);
            Assert.Equal(ExecutionStatus.Canceled, result.ExecutionStatus);
            Assert.Equal(ResultJudgement.Aborted, result.Judgement);
            var completed = Assert.Single(
                outbox,
                message => message.Kind == StationAgentMessageKinds.JobCompleted);
            using var payload = JsonDocument.Parse(completed.PayloadJson);
            Assert.Equal(
                (int)ExecutionStatus.Canceled,
                payload.RootElement.GetProperty("executionStatus").GetInt32());
            Assert.Equal(
                (int)ResultJudgement.Aborted,
                payload.RootElement.GetProperty("judgement").GetInt32());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteSqliteFiles(path);
        }
    }

    [Fact]
    public async Task SqliteStoreRetainsOfflineOutboxUntilAcknowledged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openlineops-agent-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteStationJobStore($"Data Source={path}");
            var job = CreateAcceptedJob();
            var message = new StationJobOutboxMessage(
                Guid.NewGuid(),
                job.Id,
                0,
                StationAgentMessageKinds.JobAccepted,
                "{}",
                Now,
                0,
                null,
                null);
            Assert.True(await store.TryAddAsync(job, Guid.NewGuid(), [message]));

            await store.RecordOutboxFailureAsync(message.MessageId, Now.AddSeconds(2));
            Assert.Empty(await store.ListPendingOutboxAsync(10, Now.AddSeconds(1)));
            var retry = Assert.Single(await store.ListPendingOutboxAsync(10, Now.AddSeconds(2)));
            Assert.Equal(1, retry.AttemptCount);

            await store.AcknowledgeOutboxAsync(message.MessageId, Now.AddSeconds(3));
            Assert.Empty(await store.ListPendingOutboxAsync(10, Now.AddMinutes(1)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteSqliteFiles(path);
        }
    }

    [Fact]
    public async Task SqliteOutboxRestartKeepsLaterCompletionBehindAcceptedRetryDeadline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openlineops-agent-hol-{Guid.NewGuid():N}.db");
        try
        {
            var job = CreateAcceptedJob();
            var accepted = new StationJobOutboxMessage(
                Guid.NewGuid(),
                job.Id,
                0,
                StationAgentMessageKinds.JobAccepted,
                "{}",
                Now,
                0,
                null,
                null);
            var completion = new StationJobOutboxMessage(
                Guid.NewGuid(),
                job.Id,
                1,
                StationAgentMessageKinds.JobCompleted,
                "{}",
                Now,
                0,
                null,
                null);
            using (var store = new SqliteStationJobStore($"Data Source={path}"))
            {
                Assert.True(await store.TryAddAsync(job, Guid.NewGuid(), [accepted, completion]));
                await store.RecordOutboxFailureAsync(accepted.MessageId, Now.AddSeconds(2));
            }

            using var restarted = new SqliteStationJobStore($"Data Source={path}");
            Assert.Empty(await restarted.ListPendingOutboxAsync(10, Now.AddSeconds(1)));
            var retry = Assert.Single(
                await restarted.ListPendingOutboxAsync(10, Now.AddSeconds(2)));
            Assert.Equal(accepted.MessageId, retry.MessageId);
            await restarted.AcknowledgeOutboxAsync(retry.MessageId, Now.AddSeconds(2));
            Assert.Equal(
                completion.MessageId,
                Assert.Single(await restarted.ListPendingOutboxAsync(10, Now.AddSeconds(2))).MessageId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteSqliteFiles(path);
        }
    }

    private static StationJobRequested CreateRequest(long fencingToken = 42)
    {
        using var inputs = JsonDocument.Parse(
            "{\"serialNumber\":{\"kind\":\"Text\",\"value\":\"BOARD-001\"}}");
        return new StationJobRequested(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "run/operation/attempt-1",
            "agent-station-assembly",
            "station-assembly",
            "system-station-assembly",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "operation-assemble@1",
            1,
            "product-model-board",
            "serialNumber",
            "BOARD-001",
            "lot-001",
            "carrier-001",
            "project-a",
            "application-line",
            "snapshot-001",
            "line-main",
            "topology-main",
            "operator-main",
            new string('b', 64),
            "operation-assemble",
            "flow-assemble",
            "flow-version-001",
            "config-assemble",
            "recipe-default",
            [new StationResourceFence(
                "Station",
                "system-station-assembly",
                fencingToken,
                Now.AddHours(1))],
            inputs.RootElement.Clone(),
            Now);
    }

    private static StationJobCancelRequested CreateCancellation(StationJobRequested request) => new(
        Guid.NewGuid(),
        $"{request.IdempotencyKey}/cancel",
        request.JobId,
        request.IdempotencyKey,
        request.AgentId,
        request.StationId,
        request.StationSystemId,
        request.ProductionRunId,
        request.OperationRunId,
        "operator-cancel",
        "Terminate the active vendor process tree.",
        Now);

    private static async ValueTask ApplyLeaseAsync(
        InMemoryStationResourceFenceValidator inbox,
        StationJobRequested request)
    {
        foreach (var fence in request.ResourceFences)
        {
            await inbox.ApplyAsync(new ResourceLeaseChanged(
                Guid.NewGuid(),
                $"{request.IdempotencyKey}/lease/{fence.ResourceKind}/{fence.ResourceId}/{fence.FencingToken}",
                request.AgentId,
                request.StationId,
                request.StationSystemId,
                request.JobId,
                request.ProductionRunId,
                request.OperationRunId,
                fence.ResourceKind,
                fence.ResourceId,
                fence.FencingToken,
                StationResourceLeaseStatuses.Granted,
                request.RequestedAtUtc,
                fence.ExpiresAtUtc));
        }
    }

    private static async ValueTask ApplyLeaseAsync(
        InMemoryStationResourceFenceValidator inbox,
        StationJobSnapshot job)
    {
        foreach (var fence in job.ResourceFences)
        {
            await inbox.ApplyAsync(new ResourceLeaseChanged(
                Guid.NewGuid(),
                $"{job.IdempotencyKey}/lease/{fence.ResourceKind}/{fence.ResourceId}/{fence.FencingToken}",
                job.AgentId,
                job.StationId,
                job.StationSystemId,
                job.JobId.Value,
                job.ProductionRunId,
                job.OperationRunId.Value,
                fence.ResourceKind,
                fence.ResourceId,
                fence.FencingToken,
                StationResourceLeaseStatuses.Granted,
                job.RequestedAtUtc,
                fence.ExpiresAtUtc));
        }
    }

    private static StationJob CreateAcceptedJob(long fencingToken = 42)
    {
        var request = CreateRequest(fencingToken);
        var job = StationJob.Request(new StationJobRequest(
            new StationJobId(request.JobId),
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            request.StationSystemId,
            request.ProductionRunId,
            request.ProductionUnitId,
            request.RuntimeSessionId,
            new StationOperationRunId(request.OperationRunId),
            request.OperationAttempt,
            request.ProductModelId,
            request.ProductionUnitIdentityInputKey,
            request.ProductionUnitIdentityValue,
            request.LotId,
            request.CarrierId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.ProductionLineDefinitionId,
            request.TopologyId,
            request.ActorId,
            request.PackageContentSha256,
            request.OperationId,
            request.FlowDefinitionId,
            request.FlowVersionId,
            request.ConfigurationSnapshotId,
            request.RecipeSnapshotId,
            request.ResourceFences.Select(fence => new StationResourceFenceEvidence(
                    fence.ResourceKind,
                    fence.ResourceId,
                    fence.FencingToken,
                    fence.ExpiresAtUtc))
                .ToArray(),
            JsonSerializer.Serialize(request.Inputs),
            request.RequestedAtUtc));
        job.Accept(Now);
        return job;
    }

    private static async Task<StationResourceFenceValidationResponse> ValidateViaAuthorityAsync(
        StationResourceFenceAuthorityServer authority,
        StationJobSnapshot job)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            authority.Descriptor.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await StationResourceFenceAuthorityWire.WriteAsync(
            pipe,
            new StationResourceFenceValidationRequest(
                StationOperationDocumentContract.ResourceFenceValidationRequestSchema,
                authority.Descriptor.AccessToken,
                job.JobId.Value,
                job.ProductionRunId,
                job.OperationRunId.Value,
                job.ResourceFences.Select(fence => new StationOperationResourceFence(
                    fence.ResourceKind,
                    fence.ResourceId,
                    fence.FencingToken,
                    fence.ExpiresAtUtc)).ToArray()),
            CancellationToken.None);
        return await StationResourceFenceAuthorityWire
            .ReadAsync<StationResourceFenceValidationResponse>(pipe, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static StationOperationExecutionResult Success() => new(
        ExecutionStatus.Completed,
        ResultJudgement.Passed,
        "{}",
        [],
        [],
        [],
        [],
        0,
        0,
        0,
        null,
        null);

    private static void DeleteSqliteFiles(string path)
    {
        File.Delete(path);
        File.Delete(path + "-shm");
        File.Delete(path + "-wal");
    }

    private sealed class RecordingExecutor(StationOperationExecutionResult result)
        : IStationOperationExecutor
    {
        public int ExecutionCount { get; private set; }

        public async ValueTask<StationOperationExecutionResult> ExecuteAsync(
            StationJobSnapshot job,
            Func<StationOperationProgress, CancellationToken, ValueTask> reportProgress,
            CancellationToken cancellationToken = default)
        {
            _ = job;
            ExecutionCount++;
            await reportProgress(new StationOperationProgress(50, "executing"), cancellationToken);
            return result;
        }
    }

    private sealed class RecordingIsolationCleaner : IStationRuntimeIsolationCleaner
    {
        public List<StationJobSnapshot> CleanedJobs { get; } = [];

        public ValueTask CleanupAsync(
            StationJobSnapshot job,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanedJobs.Add(job);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CleanupFailureExecutor : IStationOperationExecutor
    {
        public ValueTask<StationOperationExecutionResult> ExecuteAsync(
            StationJobSnapshot job,
            Func<StationOperationProgress, CancellationToken, ValueTask> reportProgress,
            CancellationToken cancellationToken = default) =>
            throw new StationRuntimeIsolationCleanupException(
                "Synthetic isolation cleanup failure.",
                new IOException("Synthetic locked workspace."));
    }

    private sealed class CancelableExecutor : IStationOperationExecutor
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public CancellationToken ExecutionToken { get; private set; }

        public async ValueTask<StationOperationExecutionResult> ExecuteAsync(
            StationJobSnapshot job,
            Func<StationOperationProgress, CancellationToken, ValueTask> reportProgress,
            CancellationToken cancellationToken = default)
        {
            _ = job;
            _ = reportProgress;
            ExecutionToken = cancellationToken;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Infinite delay completed without cancellation.");
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class EmptyCancellationStore : IStationSafetyInboxStore
    {
        public ValueTask<StationSafetyInboxEntry?> GetAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StationSafetyInboxEntry?>(null);

        public ValueTask<StationSafetyInboxEntry?> GetJobCancellationAsync(
            StationJobId jobId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StationSafetyInboxEntry?>(null);

        public ValueTask<bool> TryBeginAsync(
            StationSafetyInboxEntry entry,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<StationSafetyInboxEntry> CompleteAsync(
            string idempotencyKey,
            StationSafetyCommandKind commandKind,
            string requestSha256,
            string acknowledgementJson,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RunningSqliteJob : IAsyncDisposable
    {
        private RunningSqliteJob(SqliteStationJobStore store, StationJob job)
        {
            Store = store;
            Job = job;
        }

        public SqliteStationJobStore Store { get; }

        public StationJob Job { get; }

        public static async ValueTask<RunningSqliteJob> CreateAsync(string path)
        {
            var store = new SqliteStationJobStore($"Data Source={path}");
            var job = CreateAcceptedJob();
            Assert.True(await store.TryAddAsync(job, Guid.NewGuid(), []));
            job.Start(Now);
            await store.SaveAsync(job, 0, []);
            return new RunningSqliteJob(store, job);
        }

        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
