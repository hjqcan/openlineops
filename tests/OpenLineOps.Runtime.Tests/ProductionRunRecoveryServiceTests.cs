using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunRecoveryServiceTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 7, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecoveryAtUtc = CreatedAtUtc.AddHours(1);

    [Fact]
    public async Task RecoverAsyncCancelsCreatedRunAndFailsRunningStageWithoutReplayingWork()
    {
        var repository = new InMemoryProductionRunRepository();
        var sessionRepository = new InMemoryRuntimeSessionRepository();
        var created = CreateRun("created");
        var running = CreateRun("running");
        Assert.True(await repository.TryAddAsync(created));
        Assert.True(await repository.TryAddAsync(running));
        Assert.True(running.Start(CreatedAtUtc.AddSeconds(1)).Succeeded);
        var sessionId = RuntimeSessionId.New();
        Assert.True(running.StartStage(
            "stage.running.1",
            sessionId,
            CreatedAtUtc.AddSeconds(2)).Succeeded);
        var runtimeSession = CreateRunningSession(running, sessionId);
        await sessionRepository.SaveAsync(runtimeSession);
        await repository.SaveAsync(running, 0);
        var publisher = new InMemoryRuntimeDomainEventPublisher();
        var service = new ProductionRunRecoveryService(
            repository,
            sessionRepository,
            publisher,
            new FixedClock(RecoveryAtUtc));

        var result = await service.RecoverAsync();

        Assert.Equal(1, result.CanceledRunCount);
        Assert.Equal(1, result.FailedRunCount);
        Assert.Equal(0, result.CompletedRunCount);
        Assert.Equal(2, result.TotalRecoveredRuns);

        var recoveredCreated = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(created.Id)).Run;
        Assert.Equal(ProductionRunStatus.Canceled, recoveredCreated.Status);
        Assert.All(
            recoveredCreated.Stages,
            stage => Assert.Equal(ProductionStageRunStatus.Skipped, stage.Status));

        var recoveredRunning = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(running.Id)).Run;
        Assert.Equal(ProductionRunStatus.Failed, recoveredRunning.Status);
        Assert.Equal("Runtime.ProductionRunInterrupted", recoveredRunning.FailureCode);
        Assert.Contains("not replayed", recoveredRunning.FailureReason, StringComparison.Ordinal);
        Assert.Collection(
            recoveredRunning.Stages,
            interrupted =>
            {
                Assert.Equal(ProductionStageRunStatus.Failed, interrupted.Status);
                Assert.Equal(sessionId, interrupted.RuntimeSessionId);
                Assert.Equal("Runtime.ProductionRunInterrupted", interrupted.FailureCode);
                Assert.Equal(0, interrupted.CommandCount);
                Assert.Equal(1, interrupted.IncidentCount);
            },
            skipped => Assert.Equal(ProductionStageRunStatus.Skipped, skipped.Status));

        var terminalEvents = publisher.Events.OfType<ProductionRunTerminalDomainEvent>().ToArray();
        Assert.Equal(2, terminalEvents.Length);
        Assert.Contains(terminalEvents, domainEvent =>
            domainEvent.Run.RunId == running.Id
            && domainEvent.Run.Status == ProductionRunStatus.Failed
            && domainEvent.Run.Stages[0].Status == ProductionStageRunStatus.Failed);
        Assert.Empty(await repository.ListRecoverableAsync());
        var recoveredSession = Assert.IsType<RuntimeSession>(
            await sessionRepository.GetByIdAsync(sessionId));
        Assert.Equal(RuntimeSessionStatus.Failed, recoveredSession.Status);
        var interruption = Assert.Single(recoveredSession.Incidents);
        Assert.Equal("Runtime.ProductionRunInterrupted", interruption.Code);
        Assert.Contains("not replayed", interruption.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverAsyncFailsRunningRunBetweenStagesAndSkipsNextStage()
    {
        var repository = new InMemoryProductionRunRepository();
        var sessionRepository = new InMemoryRuntimeSessionRepository();
        var run = CreateRun("between");
        Assert.True(await repository.TryAddAsync(run));
        Assert.True(run.Start(CreatedAtUtc.AddSeconds(1)).Succeeded);
        Assert.True(run.StartStage(
            "stage.between.1",
            RuntimeSessionId.New(),
            CreatedAtUtc.AddSeconds(2)).Succeeded);
        Assert.True(run.CompleteStage(
            "stage.between.1",
            1,
            1,
            0,
            CreatedAtUtc.AddSeconds(3)).Succeeded);
        await repository.SaveAsync(run, 0);
        var service = new ProductionRunRecoveryService(
            repository,
            sessionRepository,
            new InMemoryRuntimeDomainEventPublisher(),
            new FixedClock(RecoveryAtUtc));

        var result = await service.RecoverAsync();

        Assert.Equal(1, result.FailedRunCount);
        var recovered = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id)).Run;
        Assert.Equal(ProductionRunStatus.Failed, recovered.Status);
        Assert.Collection(
            recovered.Stages,
            first => Assert.Equal(ProductionStageRunStatus.Completed, first.Status),
            second => Assert.Equal(ProductionStageRunStatus.Skipped, second.Status));
    }

    [Fact]
    public async Task RecoverAsyncUsesLinkedSessionTerminalTimestampWithoutReplayingLaterStages()
    {
        var repository = new InMemoryProductionRunRepository();
        var sessionRepository = new InMemoryRuntimeSessionRepository();
        var run = CreateRun("terminal-session");
        Assert.True(await repository.TryAddAsync(run));
        Assert.True(run.Start(CreatedAtUtc.AddSeconds(1)).Succeeded);
        var sessionId = RuntimeSessionId.New();
        Assert.True(run.StartStage(
            "stage.terminal-session.1",
            sessionId,
            CreatedAtUtc.AddSeconds(2)).Succeeded);
        var session = CreateRunningSession(run, sessionId);
        var sessionCompletedAtUtc = CreatedAtUtc.AddMinutes(5);
        Assert.True(session.Complete(sessionCompletedAtUtc).Succeeded);
        await sessionRepository.SaveAsync(session);
        await repository.SaveAsync(run, 0);
        var service = new ProductionRunRecoveryService(
            repository,
            sessionRepository,
            new InMemoryRuntimeDomainEventPublisher(),
            new FixedClock(RecoveryAtUtc));

        var result = await service.RecoverAsync();

        Assert.Equal(1, result.FailedRunCount);
        var recovered = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id)).Run;
        Assert.Equal(ProductionRunStatus.Failed, recovered.Status);
        Assert.Equal(RecoveryAtUtc, recovered.CompletedAtUtc);
        Assert.Collection(
            recovered.Stages,
            completed =>
            {
                Assert.Equal(ProductionStageRunStatus.Completed, completed.Status);
                Assert.Equal(sessionCompletedAtUtc, completed.CompletedAtUtc);
            },
            skipped =>
            {
                Assert.Equal(ProductionStageRunStatus.Skipped, skipped.Status);
                Assert.Equal(RecoveryAtUtc, skipped.CompletedAtUtc);
            });
    }

    private static ProductionRun CreateRun(string suffix)
    {
        return ProductionRun.Create(
            ProductionRunId.New(),
            "project.main",
            "application.main",
            $"snapshot.{suffix}",
            "topology.main",
            "line.main",
            new DutIdentity("dut.model", "dut.serial", $"SN-{suffix}"),
            null,
            null,
            null,
            "operator.recovery",
            CreatedAtUtc,
            [
                Stage($"stage.{suffix}.1", 1),
                Stage($"stage.{suffix}.2", 2)
            ]);
    }

    private static ProductionStageRunDefinition Stage(string stageId, int sequence)
    {
        return new ProductionStageRunDefinition(
            stageId,
            sequence,
            $"workstation.{sequence}",
            new StationId($"station.{sequence}"),
            new ProcessDefinitionId($"process.{sequence}"),
            new ProcessVersionId($"process.{sequence}@1.0.0"),
            new ConfigurationSnapshotId($"configuration.{sequence}"),
            new RecipeSnapshotId($"recipe.{sequence}"));
    }

    private static RuntimeSession CreateRunningSession(
        ProductionRun run,
        RuntimeSessionId sessionId)
    {
        var stage = run.Stages.Single(candidate => candidate.RuntimeSessionId == sessionId);
        var session = RuntimeSession.Create(
            sessionId,
            stage.StationId,
            stage.ProcessDefinitionId,
            stage.ProcessVersionId,
            stage.ConfigurationSnapshotId,
            stage.RecipeSnapshotId,
            CreatedAtUtc.AddSeconds(2),
            new RuntimeSessionTraceMetadata(
                run.Id,
                run.ProductionLineDefinitionId,
                stage.StageId,
                stage.Sequence,
                stage.WorkstationId,
                run.DutIdentity,
                run.BatchId,
                run.FixtureId,
                run.DeviceId,
                run.ActorId,
                run.ProjectId,
                run.ApplicationId,
                run.ProjectSnapshotId,
                run.TopologyId));
        Assert.True(session.Start(CreatedAtUtc.AddSeconds(3)).Succeeded);
        return session;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
