using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Infrastructure.Transport;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class StationResultInboxHostedService(
    RabbitMqStationCoordinatorTransport transport,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime applicationLifetime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        transport.StartResultInboxAsync(
            HandleMaterialArrivalAsync,
            HandleRecoveryRequiredAsync,
            applicationLifetime.ApplicationStopping,
            cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        transport.StopResultInboxAsync(cancellationToken);

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
