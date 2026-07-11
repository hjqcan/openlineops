using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Infrastructure.Transport;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class StationResultInboxHostedService(
    RabbitMqStationCoordinatorTransport transport,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        transport.RunResultInboxAsync(
            HandleMaterialArrivalAsync,
            HandleRecoveryRequiredAsync,
            stoppingToken);

    private async ValueTask HandleMaterialArrivalAsync(
        MaterialArrived message,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var ingress = scope.ServiceProvider.GetRequiredService<ProductionMaterialArrivalIngress>();
        _ = await ingress
            .HandleAsync(
                message,
                ProductionMaterialArrivalOrigin.StationAgent,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask HandleRecoveryRequiredAsync(
        StationJobRecoveryRequired message,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var ingress = scope.ServiceProvider
            .GetRequiredService<StationJobRecoveryRequiredIngress>();
        await ingress.HandleAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
