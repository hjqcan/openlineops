using Microsoft.Extensions.Hosting;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class SqliteRuntimeStoreLeaseHostedService(
    SqliteRuntimeStoreExclusiveLease lease) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        lease.AcquireAsync(cancellationToken).AsTask();

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lease.Release();
        return Task.CompletedTask;
    }
}
