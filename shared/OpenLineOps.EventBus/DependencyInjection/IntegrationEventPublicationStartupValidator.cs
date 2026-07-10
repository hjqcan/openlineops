using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Infrastructure.Data.Core.EventBus;

namespace OpenLineOps.EventBus.DependencyInjection;

public sealed class IntegrationEventPublicationStartupValidator(
    IServiceScopeFactory scopeFactory,
    IntegrationEventPublicationPolicy publicationPolicy) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        switch (publicationPolicy.Mode)
        {
            case IntegrationEventPublicationMode.PostCommit:
                _ = scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();
                break;
            case IntegrationEventPublicationMode.Transactional:
                _ = scope.ServiceProvider.GetRequiredService<ITransactionalIntegrationEventPublisher>();
                _ = scope.ServiceProvider.GetRequiredService<IIntegrationEventTransactionCoordinator>();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported integration event publication mode '{publicationPolicy.Mode}'.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
