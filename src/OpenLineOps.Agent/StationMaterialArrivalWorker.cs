using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent;

public sealed class StationMaterialArrivalWorker(
    StationMaterialArrivalLocalIpcServer ipcServer,
    StationMaterialArrivalOutboxDispatcher outboxDispatcher,
    IClock clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task[] loops =
        [
            ipcServer.RunAsync(lifetime.Token),
            DispatchOutboxAsync(lifetime.Token)
        ];
        await AwaitLoopsAsync(
                loops,
                lifetime,
                stoppingToken)
            .ConfigureAwait(false);
    }

    internal static Task AwaitLoopsAsync(
        Task[] loops,
        CancellationTokenSource lifetime,
        CancellationToken stoppingToken,
        TimeSpan? unexpectedSiblingDrainTimeout = null) =>
        StationAgentWorkerLifecycle.AwaitLoopsAsync(
            loops,
            lifetime,
            "Station material-arrival worker",
            stoppingToken,
            unexpectedSiblingDrainTimeout);

    private async Task DispatchOutboxAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        do
        {
            var dispatched = await outboxDispatcher
                .DispatchPendingAsync(100, UtcNow(), stoppingToken)
                .ConfigureAwait(false);
            if (dispatched == 100)
            {
                continue;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private DateTimeOffset UtcNow()
    {
        var value = clock.UtcNow;
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Station material arrival worker clock must return non-default UTC.");
        }

        return value;
    }
}
