using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class ProductionRunCoordinatorHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ProductionRunCoordinatorHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var discoveryScope = scopeFactory.CreateAsyncScope();
                var repository = discoveryScope.ServiceProvider
                    .GetRequiredService<IProductionRunRepository>();
                var active = await repository.ListActiveAsync(cancellationToken: stoppingToken)
                    .ConfigureAwait(false);
                var dispatchableIds = active
                    .Where(entry => entry.Run.ControlState == ProductionRunControlState.Active)
                    .Select(entry => entry.Run.Id)
                    .ToArray();
                await Task.WhenAll(dispatchableIds.Select(runId =>
                    ExecuteRunAsync(runId, stoppingToken))).ConfigureAwait(false);
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

    private async Task ExecuteRunAsync(
        OpenLineOps.Runtime.Domain.Identifiers.ProductionRunId runId,
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
