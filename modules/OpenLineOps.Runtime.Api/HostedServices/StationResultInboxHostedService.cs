using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Infrastructure.Transport;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class StationResultInboxHostedService(
    RabbitMqStationCoordinatorTransport transport,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        transport.RunResultInboxAsync(HandleMaterialArrivalAsync, stoppingToken);

    private async ValueTask HandleMaterialArrivalAsync(
        MaterialArrived message,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var ingress = scope.ServiceProvider.GetRequiredService<ProductionMaterialArrivalIngress>();
        _ = await ingress.HandleAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
