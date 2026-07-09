using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeSessionRecoveryServiceTests
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateRecoveryPlanAsyncReturnsOnlyNonTerminalSessions()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var running = CreateSession("running", BaseTimeUtc.AddMinutes(2));
        running.Start(BaseTimeUtc.AddMinutes(3));

        var paused = CreateSession("paused", BaseTimeUtc.AddMinutes(4));
        paused.Start(BaseTimeUtc.AddMinutes(5));
        paused.RequestPause(BaseTimeUtc.AddMinutes(6), "operator pause");
        paused.ConfirmPaused(BaseTimeUtc.AddMinutes(7), "devices paused");

        var completed = CreateSession("completed", BaseTimeUtc.AddMinutes(8));
        completed.Start(BaseTimeUtc.AddMinutes(9));
        completed.Complete(BaseTimeUtc.AddMinutes(10));

        var failed = CreateSession("failed", BaseTimeUtc.AddMinutes(11));
        failed.Start(BaseTimeUtc.AddMinutes(12));
        failed.Fail(BaseTimeUtc.AddMinutes(13), "Runtime.TestFailure", "test failure");

        await repository.SaveAsync(completed);
        await repository.SaveAsync(paused);
        await repository.SaveAsync(failed);
        await repository.SaveAsync(running);

        var service = new RuntimeSessionRecoveryService(repository);

        var plan = await service.CreateRecoveryPlanAsync();

        Assert.Equal(2, plan.Count);
        Assert.Collection(
            plan.Candidates,
            first =>
            {
                Assert.Equal(running.Id, first.SessionId);
                Assert.Equal("snapshot-running", first.ConfigurationSnapshotId.Value);
                Assert.Equal(RuntimeSessionStatus.Running, first.Status);
                Assert.Equal("Session was running during shutdown.", first.RecoveryReason);
            },
            second =>
            {
                Assert.Equal(paused.Id, second.SessionId);
                Assert.Equal("snapshot-paused", second.ConfigurationSnapshotId.Value);
                Assert.Equal(RuntimeSessionStatus.Paused, second.Status);
                Assert.Equal("Session was paused during shutdown.", second.RecoveryReason);
            });
    }

    [Fact]
    public async Task CreateRecoveryPlanAsyncReturnsEmptyPlanWhenOnlyTerminalSessionsExist()
    {
        var repository = new InMemoryRuntimeSessionRepository();
        var canceled = CreateSession("canceled", BaseTimeUtc);
        canceled.Start(BaseTimeUtc.AddSeconds(1));
        canceled.Cancel(BaseTimeUtc.AddSeconds(2), "operator cancel");

        await repository.SaveAsync(canceled);

        var service = new RuntimeSessionRecoveryService(repository);

        var plan = await service.CreateRecoveryPlanAsync();

        Assert.Equal(0, plan.Count);
        Assert.Empty(plan.Candidates);
    }

    private static RuntimeSession CreateSession(string suffix, DateTimeOffset createdAtUtc)
    {
        return RuntimeSession.Create(
            RuntimeSessionId.New(),
            new StationId($"station-{suffix}"),
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId($"process-packaging@{suffix}"),
            new ConfigurationSnapshotId($"snapshot-{suffix}"),
            new RecipeSnapshotId($"recipe-{suffix}"),
            createdAtUtc);
    }
}
