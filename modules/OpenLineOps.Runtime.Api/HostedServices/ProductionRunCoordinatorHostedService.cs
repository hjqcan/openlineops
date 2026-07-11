using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class ProductionRunCoordinatorHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ProductionRunCoordinatorHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly Action<ILogger, Exception?> LogIterationFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, "CoordinatorIterationFailed"),
            "Production Run coordinator iteration failed; durable runs remain available for retry.");
    private static readonly Action<ILogger, Guid, string, string, Exception?> LogDispatchRejected =
        LoggerMessage.Define<Guid, string, string>(
            LogLevel.Warning,
            new EventId(2, "RunDispatchRejected"),
            "Production Run {ProductionRunId} was not dispatched: {Code} {Message}");
    private static readonly Action<ILogger, Guid, Exception?> LogRunExecutionFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(3, "RunExecutionFailed"),
            "Production Run {ProductionRunId} execution failed; its durable state remains available for retry.");
    private static readonly Action<ILogger, int, Exception?> LogShutdownDrainTimedOut =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(4, "ShutdownDrainTimedOut"),
            "Production Run coordinator stopped waiting after the bounded drain timeout with {RunCount} local run task(s) still active.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var localRuns = new Dictionary<ProductionRunId, Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var completed in localRuns
                             .Where(static pair => pair.Value.IsCompleted)
                             .Select(static pair => pair.Key)
                             .ToArray())
                {
                    localRuns.Remove(completed);
                }

                try
                {
                    await using var discoveryScope = scopeFactory.CreateAsyncScope();
                    var repository = discoveryScope.ServiceProvider
                        .GetRequiredService<IProductionRunRepository>();
                    var active = await repository.ListActiveAsync(cancellationToken: stoppingToken)
                        .ConfigureAwait(false);
                    foreach (var runId in active
                                 .Where(entry =>
                                     entry.Run.ControlState == ProductionRunControlState.Active)
                                 .Select(entry => entry.Run.Id))
                    {
                        if (!localRuns.ContainsKey(runId))
                        {
                            localRuns.Add(
                                runId,
                                ExecuteRunObservedAsync(runId, stoppingToken));
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    LogIterationFailed(logger, exception);
                }

                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown is the normal exit path.
        }
        finally
        {
            var remaining = localRuns.Values.Where(static task => !task.IsCompleted).ToArray();
            if (remaining.Length > 0)
            {
                var drain = Task.WhenAll(remaining);
                if (await Task.WhenAny(
                        drain,
                        Task.Delay(ShutdownDrainTimeout, CancellationToken.None))
                    .ConfigureAwait(false) != drain)
                {
                    LogShutdownDrainTimedOut(logger, remaining.Length, null);
                }
            }
        }
    }

    private async Task ExecuteRunObservedAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteRunAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The durable run remains recoverable after host shutdown.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogRunExecutionFailed(logger, runId.Value, exception);
        }
    }

    private async Task ExecuteRunAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<IProductionRunRunner>();
        var result = await runner.ExecuteAsync(runId, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            LogDispatchRejected(
                logger,
                runId.Value,
                result.Error.Code,
                result.Error.Message,
                null);
        }
    }
}
