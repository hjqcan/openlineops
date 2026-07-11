using System.Text.Json;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Agent.Tests;

public sealed class StationJobCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandlePersistsInboxProgressAndCompletedResultBeforePublish()
    {
        var store = new InMemoryStationJobStore();
        var executor = new RecordingExecutor(new StationOperationExecutionResult(
            ExecutionStatus.Completed,
            ResultJudgement.Failed,
            "{\"measuredVoltage\":0.4}",
            [new StationOperationArtifact("reports/result.json", "application/json", 23, new string('a', 64))],
            null,
            null));
        var coordinator = new StationJobCoordinator(
            store,
            executor,
            new InMemoryStationResourceFenceValidator(),
            new FixedClock(Now));
        var request = CreateRequest();

        var result = await coordinator.HandleAsync(request);
        var duplicate = await coordinator.HandleAsync(request);
        var outbox = await store.ListPendingOutboxAsync(10, Now);

        Assert.Equal(StationJobStatus.Completed, result.Status);
        Assert.Equal(ExecutionStatus.Completed, result.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, result.Judgement);
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
                StationAgentMessageKinds.JobCompleted
            ],
            outbox.Select(message => message.Kind));
    }

    [Fact]
    public async Task HandleRejectsReusedIdempotencyKeyWithDifferentEvidence()
    {
        var coordinator = new StationJobCoordinator(
            new InMemoryStationJobStore(),
            new RecordingExecutor(Success()),
            new InMemoryStationResourceFenceValidator(),
            new FixedClock(Now));
        var request = CreateRequest();
        await coordinator.HandleAsync(request);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.HandleAsync(request with { OperationId = "operation-inspection" }));

        Assert.Contains("reused with different", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleResourceFenceIsRejectedBeforeStationRuntimeExecutes()
    {
        var executor = new RecordingExecutor(Success());
        var coordinator = new StationJobCoordinator(
            new InMemoryStationJobStore(),
            executor,
            new InMemoryStationResourceFenceValidator(),
            new FixedClock(Now));
        var first = CreateRequest();
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
                "station-assembly",
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
    public async Task RecoverMarksRunningHardwareWorkAsRecoveryRequiredWithoutReplay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openlineops-agent-{Guid.NewGuid():N}.db");
        try
        {
            await using var fixture = await RunningSqliteJob.CreateAsync(path);
            var executor = new RecordingExecutor(Success());
            var coordinator = new StationJobCoordinator(
                fixture.Store,
                executor,
                new SqliteStationResourceFenceValidator($"Data Source={path}"),
                new FixedClock(Now));

            var recovered = await coordinator.RecoverAsync();
            var persisted = await fixture.Store.GetAsync(fixture.Job.Id);

            var item = Assert.Single(recovered);
            Assert.Equal(StationJobStatus.RecoveryRequired, item.Status);
            Assert.Equal(ExecutionStatus.Failed, item.ExecutionStatus);
            Assert.Equal(ResultJudgement.Unknown, item.Judgement);
            Assert.Equal(0, executor.ExecutionCount);
            Assert.Equal(StationJobStatus.RecoveryRequired, persisted!.Job.Status);
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

    private static StationJobRequested CreateRequest()
    {
        using var inputs = JsonDocument.Parse("{\"serialNumber\":\"BOARD-001\"}");
        return new StationJobRequested(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "run/operation/attempt-1",
            "agent-station-assembly",
            "station-assembly",
            "system-station-assembly",
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
            new string('b', 64),
            "operation-assemble",
            "flow-assemble",
            "flow-version-001",
            "config-assemble",
            "recipe-default",
            [new StationResourceFence(
                "Station",
                "station-assembly",
                42,
                Now.AddHours(1))],
            inputs.RootElement.Clone(),
            Now);
    }

    private static StationJob CreateAcceptedJob()
    {
        var request = CreateRequest();
        var job = StationJob.Request(new StationJobRequest(
            new StationJobId(request.JobId),
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            request.StationSystemId,
            request.ProductionRunId,
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

    private static StationOperationExecutionResult Success() => new(
        ExecutionStatus.Completed,
        ResultJudgement.Passed,
        "{}",
        [],
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

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
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
