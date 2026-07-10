using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunTerminalOutboxDispatcherTests
{
    [Fact]
    public async Task FailedDeliveryRemainsPendingAndSuccessfulRetryAcknowledgesExactlyOnce()
    {
        var repository = new InMemoryProductionRunRepository();
        var run = CreateTerminalRun();
        Assert.True(await repository.TryAddAsync(run));
        Assert.True(run.Cancel(
            "Canceled for outbox test.",
            0,
            0,
            0,
            DateTimeOffset.UtcNow.AddSeconds(1)).Succeeded);
        await repository.SaveAsync(run, 0);
        var handler = new RecordingHandler { Failure = new IOException("trace unavailable") };
        using var dispatcher = new ProductionRunTerminalOutboxDispatcher(repository, [handler]);

        await Assert.ThrowsAsync<IOException>(async () => await dispatcher.DrainAsync());

        var failed = Assert.Single(await repository.ListPendingTerminalOutboxAsync(10));
        Assert.Equal(1, failed.AttemptCount);
        Assert.NotNull(failed.LastError);
        Assert.Contains("trace unavailable", failed.LastError, StringComparison.Ordinal);
        handler.Failure = null;

        Assert.Equal(1, await dispatcher.DrainAsync());
        Assert.Empty(await repository.ListPendingTerminalOutboxAsync(10));
        Assert.Equal(2, handler.Received.Count);
        Assert.Equal(run.Id, handler.Received[0].RunId);
        Assert.Equal(run.Id, handler.Received[1].RunId);
        Assert.Equal(0, await dispatcher.DrainAsync());
        Assert.Equal(2, handler.Received.Count);
    }

    private static ProductionRun CreateTerminalRun()
    {
        return ProductionRun.Create(
            ProductionRunId.New(),
            "project.outbox",
            "application.outbox",
            "snapshot.outbox",
            "topology.outbox",
            "line.outbox",
            new DutIdentity("dut.outbox", "dut.serial", "SN-OUTBOX"),
            null,
            null,
            null,
            "operator.outbox",
            DateTimeOffset.UtcNow,
            [
                new ProductionStageRunDefinition(
                    "stage.outbox",
                    1,
                    "workstation.outbox",
                    new StationId("station.outbox"),
                    new ProcessDefinitionId("process.outbox"),
                    new ProcessVersionId("process.outbox@1.0.0"),
                    new ConfigurationSnapshotId("configuration.outbox"),
                    new RecipeSnapshotId("recipe.outbox"))
            ]);
    }

    private sealed class RecordingHandler : IProductionRunTerminalOutboxHandler
    {
        public List<ProductionRunSnapshot> Received { get; } = [];

        public Exception? Failure { get; set; }

        public ValueTask HandleAsync(
            ProductionRunSnapshot run,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Received.Add(run);
            return Failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(Failure);
        }
    }
}
