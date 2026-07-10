using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Targets;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsyncPersistsPreallocatedSessionBeforeEachStrictlySequentialStage()
    {
        var repository = new InMemoryProductionRunRepository();
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var observedSequences = new List<int>();
        var sessionRunner = new RecordingRuntimeSessionRunner(async request =>
        {
            var persisted = Assert.IsType<ProductionRunPersistenceEntry>(
                await repository.GetByIdAsync(request.TraceMetadata.ProductionRunId)).Run;
            var runningStage = Assert.Single(
                persisted.Stages,
                stage => stage.Status == ProductionStageRunStatus.Running);
            Assert.Equal(request.TraceMetadata.ProductionStageId, runningStage.StageId);
            Assert.Equal(request.SessionId, runningStage.RuntimeSessionId);
            Assert.All(
                persisted.Stages.Where(stage => stage.Sequence < runningStage.Sequence),
                stage => Assert.Equal(ProductionStageRunStatus.Completed, stage.Status));
            Assert.All(
                persisted.Stages.Where(stage => stage.Sequence > runningStage.Sequence),
                stage => Assert.Equal(ProductionStageRunStatus.Pending, stage.Status));
            observedSequences.Add(runningStage.Sequence);

            return Result.Success(new RuntimeSessionRunResult(
                request.SessionId,
                request.ConfigurationSnapshotId,
                RuntimeSessionStatus.Completed,
                runningStage.Sequence,
                runningStage.Sequence + 1,
                runningStage.Sequence - 1));
        });
        var runner = CreateRunner(repository, publisher, sessionRunner);
        var request = CreateRequest();

        var result = await runner.RunAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProductionRunStatus.Completed, result.Value.Run.Status);
        Assert.Equal([1, 2], observedSequences);
        Assert.Equal(2, sessionRunner.Requests.Count);
        Assert.Equal(6, repository.SaveCount);
        Assert.Collection(
            result.Value.Run.Stages,
            first =>
            {
                Assert.Equal(1, first.CompletedStepCount);
                Assert.Equal(2, first.CommandCount);
                Assert.Equal(0, first.IncidentCount);
            },
            second =>
            {
                Assert.Equal(2, second.CompletedStepCount);
                Assert.Equal(3, second.CommandCount);
                Assert.Equal(1, second.IncidentCount);
            });

        var firstMetadata = sessionRunner.Requests[0].TraceMetadata;
        Assert.Equal(request.RunId, firstMetadata.ProductionRunId);
        Assert.Equal("line.main", firstMetadata.ProductionLineDefinitionId);
        Assert.Equal("stage.scan", firstMetadata.ProductionStageId);
        Assert.Equal(1, firstMetadata.StageSequence);
        Assert.Equal("workstation.scan", firstMetadata.WorkstationId);
        Assert.Equal("dut.model", firstMetadata.DutIdentity.ModelId);
        Assert.Equal("dut.serial", firstMetadata.DutIdentity.InputKey);
        Assert.Equal("SN-100", firstMetadata.DutIdentity.Value);
        Assert.Equal("operator.1", firstMetadata.ActorId);
        Assert.Equal("SN-100", firstMetadata.DutIdentity.Value);
        Assert.IsType<ProductionRunTerminalDomainEvent>(publisher.Events.Last());
    }

    [Fact]
    public async Task FailedSessionStopsLineAndSkipsRemainingStagesWithExactMetrics()
    {
        var repository = new InMemoryProductionRunRepository();
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var sessionRunner = new RecordingRuntimeSessionRunner(request =>
            ValueTask.FromResult(Result.Success(new RuntimeSessionRunResult(
                request.SessionId,
                request.ConfigurationSnapshotId,
                RuntimeSessionStatus.Failed,
                2,
                3,
                1))));
        var runner = CreateRunner(repository, publisher, sessionRunner);

        var result = await runner.RunAsync(CreateRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(ProductionRunStatus.Failed, result.Value.Run.Status);
        Assert.Single(sessionRunner.Requests);
        Assert.Collection(
            result.Value.Run.Stages,
            failed =>
            {
                Assert.Equal(ProductionStageRunStatus.Failed, failed.Status);
                Assert.Equal(2, failed.CompletedStepCount);
                Assert.Equal(3, failed.CommandCount);
                Assert.Equal(1, failed.IncidentCount);
            },
            skipped =>
            {
                Assert.Equal(ProductionStageRunStatus.Skipped, skipped.Status);
                Assert.Null(skipped.RuntimeSessionId);
                Assert.Equal(0, skipped.CommandCount);
            });
        var terminalEvent = Assert.IsType<ProductionRunTerminalDomainEvent>(publisher.Events.Last());
        Assert.Equal(ProductionRunStatus.Failed, terminalEvent.Run.Status);
        Assert.Equal(ProductionStageRunStatus.Skipped, terminalEvent.Run.Stages[1].Status);
    }

    [Fact]
    public async Task ExistingCallerAllocatedRunIdReturnsStoredRunWithoutExecutingAStage()
    {
        var repository = new InMemoryProductionRunRepository();
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var sessionRunner = new RecordingRuntimeSessionRunner(request =>
            throw new InvalidOperationException($"Session {request.SessionId} must not execute."));
        var request = CreateRequest();
        var existing = CreateAggregate(request);
        Assert.True(await repository.TryAddAsync(existing));
        var runner = CreateRunner(repository, publisher, sessionRunner);

        var result = await runner.RunAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProductionRunStatus.Created, result.Value.Run.Status);
        Assert.Equal(request.RunId, result.Value.Run.RunId);
        Assert.Empty(sessionRunner.Requests);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public async Task ExistingRunIdWithDifferentImmutableIdentityReturnsConflict()
    {
        var repository = new InMemoryProductionRunRepository();
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var sessionRunner = new RecordingRuntimeSessionRunner(request =>
            throw new InvalidOperationException($"Session {request.SessionId} must not execute."));
        var request = CreateRequest();
        Assert.True(await repository.TryAddAsync(CreateAggregate(request)));
        var mismatched = new StartProductionRunRequest(
            request.RunId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TopologyId,
            request.ProductionLineDefinitionId,
            new DutIdentity(
                request.DutIdentity.ModelId,
                request.DutIdentity.InputKey,
                "SN-DIFFERENT"),
            request.ActorId,
            request.Stages,
            request.BatchId,
            request.FixtureId,
            request.DeviceId);
        var runner = CreateRunner(repository, publisher, sessionRunner);

        var result = await runner.RunAsync(mismatched);

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Runtime.ProductionRunIdIdentityMismatch", result.Error.Code);
        Assert.Empty(sessionRunner.Requests);
    }

    [Fact]
    public async Task ConcurrentCallsWithSameRunIdExecuteExactlyOneProductionRun()
    {
        var repository = new InMemoryProductionRunRepository();
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var firstStageEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstStage = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionRunner = new RecordingRuntimeSessionRunner(async request =>
        {
            firstStageEntered.TrySetResult();
            await releaseFirstStage.Task;
            return Result.Success(new RuntimeSessionRunResult(
                request.SessionId,
                request.ConfigurationSnapshotId,
                RuntimeSessionStatus.Completed,
                CompletedSteps: 1,
                CommandCount: 1,
                IncidentCount: 0));
        });
        var runner = CreateRunner(repository, publisher, sessionRunner);
        var request = CreateRequest();

        var acceptedTask = runner.RunAsync(request).AsTask();
        await firstStageEntered.Task;
        var replay = await runner.RunAsync(request);
        releaseFirstStage.SetResult();
        var accepted = await acceptedTask;

        Assert.True(accepted.IsSuccess);
        Assert.Equal(ProductionRunStatus.Completed, accepted.Value.Run.Status);
        Assert.True(replay.IsSuccess);
        Assert.Equal(ProductionRunStatus.Running, replay.Value.Run.Status);
        Assert.Equal(2, sessionRunner.Requests.Count);
        Assert.Equal(6, repository.SaveCount);
    }

    [Fact]
    public async Task UnexpectedSessionBoundaryExceptionBecomesPersistedTerminalFailure()
    {
        var repository = new InMemoryProductionRunRepository();
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var sessionRunner = new RecordingRuntimeSessionRunner(_ =>
            throw new InvalidOperationException("driver transport fault"));
        var runner = CreateRunner(repository, publisher, sessionRunner);
        var request = CreateRequest();

        var result = await runner.RunAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProductionRunStatus.Failed, result.Value.Run.Status);
        Assert.Equal("Runtime.ProductionStageExecutionFault", result.Value.Run.FailureCode);
        Assert.Contains(
            typeof(InvalidOperationException).FullName!,
            result.Value.Run.FailureReason,
            StringComparison.Ordinal);
        Assert.Collection(
            result.Value.Run.Stages,
            failed => Assert.Equal(ProductionStageRunStatus.Failed, failed.Status),
            skipped => Assert.Equal(ProductionStageRunStatus.Skipped, skipped.Status));
        var persisted = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId)).Run;
        Assert.True(persisted.IsTerminal);
        Assert.IsType<ProductionRunTerminalDomainEvent>(publisher.Events.Last());
    }

    [Fact]
    public async Task PersistedTerminalSessionIsReconciledWhenSessionBoundaryThrowsAfterSave()
    {
        var repository = new InMemoryProductionRunRepository();
        var sessionRepository = new InMemoryRuntimeSessionRepository();
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var sessionRunner = new RecordingRuntimeSessionRunner(async request =>
        {
            var session = RuntimeSession.Create(
                request.SessionId,
                request.StationId,
                request.Process.ProcessDefinitionId,
                request.Process.ProcessVersionId,
                request.ConfigurationSnapshotId,
                request.RecipeSnapshotId,
                Now,
                request.TraceMetadata);
            Assert.True(session.Start(Now.AddSeconds(1)).Succeeded);
            Assert.True(session.Complete(Now.AddSeconds(2)).Succeeded);
            await sessionRepository.SaveAsync(session);
            throw new InvalidOperationException("subscriber failed after terminal save");
        });
        var runner = CreateRunner(
            repository,
            publisher,
            sessionRunner,
            sessionRepository);

        var result = await runner.RunAsync(CreateRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(ProductionRunStatus.Completed, result.Value.Run.Status);
        Assert.All(
            result.Value.Run.Stages,
            stage => Assert.Equal(ProductionStageRunStatus.Completed, stage.Status));
        Assert.Equal(2, sessionRunner.Requests.Count);
    }

    [Fact]
    public async Task CanceledSessionTerminalStatePersistsEvenWhenCallerTokenCancelsConcurrently()
    {
        var repository = new InMemoryProductionRunRepository();
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        using var cancellation = new CancellationTokenSource();
        var sessionRunner = new RecordingRuntimeSessionRunner(request =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(Result.Success(new RuntimeSessionRunResult(
                request.SessionId,
                request.ConfigurationSnapshotId,
                RuntimeSessionStatus.Canceled,
                CompletedSteps: 1,
                CommandCount: 2,
                IncidentCount: 0)));
        });
        var runner = CreateRunner(repository, publisher, sessionRunner);
        var request = CreateRequest();

        var result = await runner.RunAsync(request, cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProductionRunStatus.Canceled, result.Value.Run.Status);
        var persisted = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(request.RunId)).Run;
        Assert.Equal(ProductionRunStatus.Canceled, persisted.Status);
        Assert.Collection(
            persisted.Stages,
            canceled =>
            {
                Assert.Equal(ProductionStageRunStatus.Canceled, canceled.Status);
                Assert.Equal(1, canceled.CompletedStepCount);
                Assert.Equal(2, canceled.CommandCount);
            },
            skipped => Assert.Equal(ProductionStageRunStatus.Skipped, skipped.Status));
    }

    [Fact]
    public void StagePlanDefensivelyFreezesExecutableProcess()
    {
        var nodes = new List<ExecutableRuntimeNode> { Node("node.one") };
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.one"),
            new ProcessVersionId("process.one@1.0.0"),
            nodes);
        var plan = Plan("stage.one", 1, "workstation.one", process);

        nodes.Add(Node("node.two"));

        Assert.Single(plan.FrozenExecutableProcess.Nodes);
        Assert.Equal("node.one", plan.FrozenExecutableProcess.Nodes[0].NodeId.Value);
    }

    private static ProductionRunRunner CreateRunner(
        InMemoryProductionRunRepository repository,
        InMemoryRuntimeDomainEventPublisher publisher,
        IRuntimeSessionRunner sessionRunner,
        InMemoryRuntimeSessionRepository? sessionRepository = null)
    {
        return new ProductionRunRunner(
            repository,
            sessionRepository ?? new InMemoryRuntimeSessionRepository(),
            sessionRunner,
            publisher,
            new DeterministicRuntimeIdProvider(),
            new FixedClock(Now));
    }

    private static StartProductionRunRequest CreateRequest()
    {
        return new StartProductionRunRequest(
            new ProductionRunId(Guid.Parse("40000000-0000-0000-0000-000000000001")),
            "project.main",
            "application.main",
            "snapshot.release",
            "topology.main",
            "line.main",
            new DutIdentity("dut.model", "dut.serial", "SN-100"),
            "operator.1",
            [
                Plan("stage.scan", 1, "workstation.scan", Process("scan")),
                Plan("stage.test", 2, "workstation.test", Process("test"))
            ],
            "batch.1",
            "fixture.1",
            "device.1");
    }

    private static ProductionRun CreateAggregate(StartProductionRunRequest request)
    {
        return ProductionRun.Create(
            request.RunId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.TopologyId,
            request.ProductionLineDefinitionId,
            request.DutIdentity,
            request.BatchId,
            request.FixtureId,
            request.DeviceId,
            request.ActorId,
            Now,
            request.Stages.Select(plan => new ProductionStageRunDefinition(
                plan.StageId,
                plan.Sequence,
                plan.WorkstationId,
                plan.StationId,
                plan.FrozenExecutableProcess.ProcessDefinitionId,
                plan.FrozenExecutableProcess.ProcessVersionId,
                plan.ConfigurationSnapshotId,
                plan.RecipeSnapshotId)));
    }

    private static ProductionStageExecutionPlan Plan(
        string stageId,
        int sequence,
        string workstationId,
        ExecutableRuntimeProcess process)
    {
        return new ProductionStageExecutionPlan(
            "line.main",
            stageId,
            sequence,
            workstationId,
            new StationId($"station.{sequence}"),
            new ConfigurationSnapshotId($"configuration.{sequence}"),
            new RecipeSnapshotId($"recipe.{sequence}"),
            process);
    }

    private static ExecutableRuntimeProcess Process(string suffix)
    {
        return new ExecutableRuntimeProcess(
            new ProcessDefinitionId($"process.{suffix}"),
            new ProcessVersionId($"process.{suffix}@1.0.0"),
            [Node($"node.{suffix}")]);
    }

    private static ExecutableRuntimeNode Node(string nodeId)
    {
        return new ExecutableRuntimeNode(
            new RuntimeNodeId(nodeId),
            nodeId,
            new RuntimeCapabilityId("capability.execute"),
            "Execute",
            TimeSpan.FromSeconds(1),
            null,
            new RuntimeActionId($"{nodeId}:action"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "system.main"));
    }

    private sealed class RecordingRuntimeSessionRunner(
        Func<StartRuntimeSessionRequest, ValueTask<Result<RuntimeSessionRunResult>>> execute)
        : IRuntimeSessionRunner
    {
        public List<StartRuntimeSessionRequest> Requests { get; } = [];

        public ValueTask<Result<RuntimeSessionRunResult>> RunAsync(
            StartRuntimeSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return execute(request);
        }
    }

    private sealed class DeterministicRuntimeIdProvider : IRuntimeIdProvider
    {
        private int _value;

        public RuntimeSessionId NewSessionId() => new(NextGuid());

        public RuntimeStepId NewStepId() => new(NextGuid());

        public RuntimeCommandId NewCommandId() => new(NextGuid());

        private Guid NextGuid()
        {
            _value++;
            return Guid.Parse($"50000000-0000-0000-0000-{_value:000000000000}");
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
