using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent;

public sealed class StationMaterialArrivalWorker(
    StationMaterialArrivalLocalIpcServer ipcServer,
    StationMaterialArrivalOutboxDispatcher outboxDispatcher,
    IClock clock) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            ipcServer.RunAsync(stoppingToken),
            DispatchOutboxAsync(stoppingToken));

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
