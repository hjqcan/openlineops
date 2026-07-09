using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Domain.Abstractions.Repositories;
using OpenLineOps.Infrastructure.Data.Core.EventBus;
using NetDevPackDomainEvent = NetDevPack.Messaging.DomainEvent;
using NetDevPackDomainEventDispatcher = NetDevPack.Messaging.IDomainEventDispatcher;
using NetDevPackEvent = NetDevPack.Messaging.Event;

namespace OpenLineOps.Infrastructure.Data.Core.Context;

public abstract class BaseDbContext : DbContext, IUnitOfWork
{
    private static readonly Action<ILogger, Exception?> LogCommitCompletedWithNoRows =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(1, nameof(LogCommitCompletedWithNoRows)),
            "Commit completed with no persisted rows; domain events were not dispatched.");

    private static readonly Action<ILogger, Exception?> LogCommitFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(LogCommitFailed)),
            "Commit failed while saving changes.");

    private static readonly Action<ILogger, int, Exception?> LogNoOpenLineOpsDispatcher =
        LoggerMessage.Define<int>(
            LogLevel.Debug,
            new EventId(3, nameof(LogNoOpenLineOpsDispatcher)),
            "No OpenLineOps domain event dispatcher was configured; skipped {Count} events.");

    private static readonly Action<ILogger, string, Exception?> LogOpenLineOpsDispatchFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(4, nameof(LogOpenLineOpsDispatchFailed)),
            "OpenLineOps domain event dispatch failed. CorrelationId: {CorrelationId}");

    private static readonly Action<ILogger, int, Exception?> LogNoNetDevPackDispatcher =
        LoggerMessage.Define<int>(
            LogLevel.Debug,
            new EventId(5, nameof(LogNoNetDevPackDispatcher)),
            "No NetDevPack domain event dispatcher was configured; skipped {Count} events.");

    private static readonly Action<ILogger, string, Exception?> LogNetDevPackDispatchFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(6, nameof(LogNetDevPackDispatchFailed)),
            "NetDevPack domain event dispatch failed. CorrelationId: {CorrelationId}");

    private static readonly Action<ILogger, Exception?> LogNoIntegrationPublisher =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(7, nameof(LogNoIntegrationPublisher)),
            "No integration event publisher was configured.");

    private static readonly Action<ILogger, string, Exception?> LogIntegrationPublishFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(8, nameof(LogIntegrationPublishFailed)),
            "Integration event publishing failed. CorrelationId: {CorrelationId}");

    private readonly IDomainEventDispatcher? _domainEventDispatcher;
    private readonly NetDevPackDomainEventDispatcher? _netDevPackDomainEventDispatcher;
    private readonly IIntegrationEventPublisher? _integrationEventPublisher;
    private readonly IIntegrationEventTransactionCoordinator? _integrationEventTransactionCoordinator;
    private readonly ILogger<BaseDbContext>? _logger;

    protected BaseDbContext(
        DbContextOptions options,
        IDomainEventDispatcher? domainEventDispatcher = null,
        NetDevPackDomainEventDispatcher? netDevPackDomainEventDispatcher = null,
        IIntegrationEventPublisher? integrationEventPublisher = null,
        IIntegrationEventTransactionCoordinator? integrationEventTransactionCoordinator = null,
        ILogger<BaseDbContext>? logger = null)
        : base(options)
    {
        _domainEventDispatcher = domainEventDispatcher;
        _netDevPackDomainEventDispatcher = netDevPackDomainEventDispatcher;
        _integrationEventPublisher = integrationEventPublisher;
        _integrationEventTransactionCoordinator = integrationEventTransactionCoordinator;
        _logger = logger;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Ignore<NetDevPackEvent>();

        base.OnModelCreating(modelBuilder);
    }

    public async Task<bool> Commit()
    {
        return await CommitAsync().ConfigureAwait(false);
    }

    public async Task<bool> CommitAsync(CancellationToken cancellationToken = default)
    {
        var openLineOpsEntities = ChangeTracker
            .Entries()
            .Select(entry => entry.Entity)
            .OfType<IHasDomainEvents>()
            .Where(entity => entity.DomainEvents.Count > 0)
            .ToList();

        var openLineOpsDomainEvents = openLineOpsEntities
            .SelectMany(entity => entity.DomainEvents)
            .ToArray();

        foreach (var entity in openLineOpsEntities)
        {
            entity.ClearDomainEvents();
        }

        var netDevPackEntities = ChangeTracker
            .Entries<NetDevPack.Domain.Entity>()
            .Where(entry => entry.Entity.DomainEvents is { Count: > 0 })
            .Select(entry => entry.Entity)
            .ToList();

        var netDevPackEvents = netDevPackEntities
            .SelectMany(entity => entity.DomainEvents ?? Array.Empty<NetDevPackEvent>())
            .ToArray();

        foreach (var entity in netDevPackEntities)
        {
            entity.ClearDomainEvents();
        }

        var integrationEvents = GetIntegrationEvents(
            openLineOpsDomainEvents.Cast<object>().Concat(netDevPackEvents));
        var integrationEventsPublishedInTransaction = false;

        var affectedRows = await SaveChangesAndPublishTransactionalIntegrationEventsAsync(
            integrationEvents,
            cancellationToken).ConfigureAwait(false);
        integrationEventsPublishedInTransaction = affectedRows.PublishedIntegrationEventsInTransaction;
        if (affectedRows.AffectedRows <= 0)
        {
            if (_logger is not null)
            {
                LogCommitCompletedWithNoRows(_logger, null);
            }

            return false;
        }

        await DispatchOpenLineOpsDomainEventsAsync(openLineOpsDomainEvents, cancellationToken)
            .ConfigureAwait(false);
        await DispatchNetDevPackDomainEventsAsync(netDevPackEvents, cancellationToken)
            .ConfigureAwait(false);
        if (!integrationEventsPublishedInTransaction)
        {
            await PublishIntegrationEventsAsync(integrationEvents, cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    private async Task<CommitPersistenceResult> SaveChangesAndPublishTransactionalIntegrationEventsAsync(
        object[] integrationEvents,
        CancellationToken cancellationToken)
    {
        try
        {
            if (CanPublishIntegrationEventsInTransaction(integrationEvents))
            {
                var affectedRows = await _integrationEventTransactionCoordinator!
                    .SaveChangesAndPublishAsync(
                        this,
                        SaveChangesAsync,
                        token => ((ITransactionalIntegrationEventPublisher)_integrationEventPublisher!)
                            .PublishTransactionalAsync(integrationEvents, token),
                        cancellationToken)
                    .ConfigureAwait(false);

                return new CommitPersistenceResult(
                    affectedRows,
                    PublishedIntegrationEventsInTransaction: affectedRows > 0);
            }

            return new CommitPersistenceResult(
                await SaveChangesAsync(cancellationToken).ConfigureAwait(false),
                PublishedIntegrationEventsInTransaction: false);
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogCommitFailed(_logger, ex);
            }

            throw;
        }
    }

    private bool CanPublishIntegrationEventsInTransaction(object[] integrationEvents)
    {
        return integrationEvents.Length > 0
            && _integrationEventTransactionCoordinator is not null
            && _integrationEventPublisher is ITransactionalIntegrationEventPublisher;
    }

    private async Task DispatchOpenLineOpsDomainEventsAsync(
        IDomainEvent[] domainEvents,
        CancellationToken cancellationToken)
    {
        if (domainEvents.Length == 0)
        {
            return;
        }

        if (_domainEventDispatcher is null)
        {
            if (_logger is not null)
            {
                LogNoOpenLineOpsDispatcher(_logger, domainEvents.Length, null);
            }

            return;
        }

        try
        {
            await _domainEventDispatcher.DispatchAsync(domainEvents, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogOpenLineOpsDispatchFailed(_logger, CurrentCorrelationId(), ex);
            }
        }
    }

    private async Task DispatchNetDevPackDomainEventsAsync(
        NetDevPackEvent[] domainEvents,
        CancellationToken cancellationToken)
    {
        if (domainEvents.Length == 0)
        {
            return;
        }

        var localDomainEvents = domainEvents.OfType<NetDevPackDomainEvent>().ToArray();
        if (localDomainEvents.Length > 0)
        {
            if (_netDevPackDomainEventDispatcher is null)
            {
                if (_logger is not null)
                {
                    LogNoNetDevPackDispatcher(_logger, localDomainEvents.Length, null);
                }
            }
            else
            {
                try
                {
                    await _netDevPackDomainEventDispatcher.DispatchAsync(localDomainEvents).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_logger is not null)
                    {
                        LogNetDevPackDispatchFailed(_logger, CurrentCorrelationId(), ex);
                    }
                }
            }
        }
    }

    private async Task PublishIntegrationEventsAsync(
        object[] eventsToPublish,
        CancellationToken cancellationToken)
    {
        if (eventsToPublish.Length == 0)
        {
            return;
        }

        if (_integrationEventPublisher is null)
        {
            if (_logger is not null)
            {
                LogNoIntegrationPublisher(_logger, null);
            }

            return;
        }

        try
        {
            await _integrationEventPublisher.PublishAsync(eventsToPublish, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogIntegrationPublishFailed(_logger, CurrentCorrelationId(), ex);
            }
        }
    }

    private static string CurrentCorrelationId()
    {
        return Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
    }

    private static object[] GetIntegrationEvents(IEnumerable<object> domainEvents)
    {
        return domainEvents
            .Where(IntegrationEventDescriptorFactory.IsIntegrationEvent)
            .ToArray();
    }

    private readonly record struct CommitPersistenceResult(
        int AffectedRows,
        bool PublishedIntegrationEventsInTransaction);
}
