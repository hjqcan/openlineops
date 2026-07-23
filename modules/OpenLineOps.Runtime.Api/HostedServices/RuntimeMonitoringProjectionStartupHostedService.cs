using Microsoft.Extensions.Hosting;
using OpenLineOps.Runtime.Application.Monitoring;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class RuntimeMonitoringProjectionStartupHostedService(
    IRuntimeMonitoringProjectionInitializer initializer) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
