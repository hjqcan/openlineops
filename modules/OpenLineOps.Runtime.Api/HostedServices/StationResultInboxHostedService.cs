using Microsoft.Extensions.Hosting;
using OpenLineOps.Runtime.Infrastructure.Transport;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class StationResultInboxHostedService(
    RabbitMqStationCoordinatorTransport transport) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        transport.RunResultInboxAsync(stoppingToken);
}
