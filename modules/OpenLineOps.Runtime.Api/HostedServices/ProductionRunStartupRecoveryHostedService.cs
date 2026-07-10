using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Runtime.Application.Recovery;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class ProductionRunStartupRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    IEnumerable<SqliteRuntimeStoreExclusiveLease> sqliteStoreLeases)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var lease in sqliteStoreLeases)
        {
            lease.EnsureAcquired();
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var recoveryService = scope.ServiceProvider
            .GetRequiredService<IProductionRunRecoveryService>();
        _ = await recoveryService.RecoverAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
