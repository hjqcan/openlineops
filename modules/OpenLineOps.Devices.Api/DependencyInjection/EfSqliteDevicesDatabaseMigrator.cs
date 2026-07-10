using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Devices.Infrastructure.Persistence.Ef;

namespace OpenLineOps.Devices.Api.DependencyInjection;

internal sealed class EfSqliteDevicesDatabaseMigrator(IServiceScopeFactory scopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<DevicesDbContext>();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
