using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Domain.Abstractions.Repositories;
using OpenLineOps.Infrastructure.Data.Core.EventBus;
using NetDevPackEvent = NetDevPack.Messaging.Event;

namespace OpenLineOps.Infrastructure.Data.Core.Context;

public abstract class BaseDbContext : DbContext, IUnitOfWork
{
    private static readonly Action<ILogger, Exception?> LogCommitCompletedWithNoRows =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(1, nameof(LogCommitCompletedWithNoRows)),
            "Commit completed with no persisted rows or pending domain events.");

    private static readonly Action<ILogger, Exception?> LogCommitFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(LogCommitFailed)),
            "Commit failed while saving changes or publishing transactional integration events.");

    private static readonly Action<ILogger, string, Exception?> LogIntegrationPublishFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(3, nameof(LogIntegrationPublishFailed)),
            "Integration event publishing failed. CorrelationId: {CorrelationId}");

    private readonly IntegrationEventPublicationPolicy? _integrationEventPublicationPolicy;
    private readonly IIntegrationEventPublisher? _integrationEventPublisher;
    private readonly ITransactionalIntegrationEventPublisher? _transactionalIntegrationEventPublisher;
    private readonly IIntegrationEventTransactionCoordinator? _integrationEventTransactionCoordinator;
    private readonly ILogger<BaseDbContext>? _logger;

    protected BaseDbContext(
        DbContextOptions options,
        IntegrationEventPublicationPolicy? integrationEventPublicationPolicy = null,
        IIntegrationEventPublisher? integrationEventPublisher = null,
        ITransactionalIntegrationEventPublisher? transactionalIntegrationEventPublisher = null,
        IIntegrationEventTransactionCoordinator? integrationEventTransactionCoordinator = null,
        ILogger<BaseDbContext>? logger = null)
        : base(options)
    {
        _integrationEventPublicationPolicy = integrationEventPublicationPolicy;
        _integrationEventPublisher = integrationEventPublisher;
        _transactionalIntegrationEventPublisher = transactionalIntegrationEventPublisher;
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
        var pendingEvents = CapturePendingEvents();
        var integrationEvents = pendingEvents
            .Where(pendingEvent => IntegrationEventDescriptorFactory.IsIntegrationEvent(pendingEvent.Payload))
            .ToArray();

        PreflightLocalDomainEvents(pendingEvents, integrationEvents);
        PreflightIntegrationEventPublication(integrationEvents);

        if (integrationEvents.Length > 0
            && _integrationEventPublicationPolicy!.Mode == IntegrationEventPublicationMode.Transactional)
        {
            return await CommitTransactionallyAsync(
                    pendingEvents,
                    integrationEvents,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await CommitPostCommitAsync(
                pendingEvents,
                integrationEvents,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> CommitTransactionallyAsync(
        IReadOnlyCollection<PendingDomainEvent> pendingEvents,
        IReadOnlyCollection<PendingDomainEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        try
        {
            var affectedRows = await _integrationEventTransactionCoordinator!
                .SaveChangesAndPublishAsync(
                    this,
                    token => SaveChangesAsync(acceptAllChangesOnSuccess: false, token),
                    token => PublishTransactionalIntegrationEventsAsync(integrationEvents, token),
                    cancellationToken)
                .ConfigureAwait(false);

            ChangeTracker.AcceptAllChanges();
            RemoveEvents(pendingEvents);

            return affectedRows > 0 || pendingEvents.Count > 0;
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

    private async Task<bool> CommitPostCommitAsync(
        IReadOnlyCollection<PendingDomainEvent> pendingEvents,
        IReadOnlyCollection<PendingDomainEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        var affectedRows = await SaveChangesWithLoggingAsync(cancellationToken).ConfigureAwait(false);

        var integrationEventSet = integrationEvents.ToHashSet();
        RemoveEvents(pendingEvents.Where(pendingEvent => !integrationEventSet.Contains(pendingEvent)));

        foreach (var pendingEvent in integrationEvents)
        {
            try
            {
                await _integrationEventPublisher!
                    .PublishAsync([pendingEvent.Payload], cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger is not null)
                {
                    LogIntegrationPublishFailed(_logger, CurrentCorrelationId(), ex);
                }

                throw;
            }

            pendingEvent.Remove();
        }

        var committed = affectedRows > 0 || pendingEvents.Count > 0;
        if (!committed && _logger is not null)
        {
            LogCommitCompletedWithNoRows(_logger, null);
        }

        return committed;
    }

    private async Task<int> SaveChangesWithLoggingAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task PublishTransactionalIntegrationEventsAsync(
        IReadOnlyCollection<PendingDomainEvent> integrationEvents,
        CancellationToken cancellationToken)
    {
        try
        {
            await _transactionalIntegrationEventPublisher!
                .PublishTransactionalAsync(
                    integrationEvents.Select(pendingEvent => pendingEvent.Payload),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogIntegrationPublishFailed(_logger, CurrentCorrelationId(), ex);
            }

            throw;
        }
    }

    private void PreflightIntegrationEventPublication(
        IReadOnlyCollection<PendingDomainEvent> integrationEvents)
    {
        if (integrationEvents.Count == 0)
        {
            return;
        }

        if (_integrationEventPublicationPolicy is null)
        {
            throw new InvalidOperationException(
                "An explicit integration event publication policy is required before committing integration events.");
        }

        switch (_integrationEventPublicationPolicy.Mode)
        {
            case IntegrationEventPublicationMode.PostCommit when _integrationEventPublisher is null:
                throw new InvalidOperationException(
                    "PostCommit integration event publication requires IIntegrationEventPublisher before saving changes.");
            case IntegrationEventPublicationMode.Transactional when _transactionalIntegrationEventPublisher is null:
                throw new InvalidOperationException(
                    "Transactional integration event publication requires ITransactionalIntegrationEventPublisher before saving changes.");
            case IntegrationEventPublicationMode.Transactional when _integrationEventTransactionCoordinator is null:
                throw new InvalidOperationException(
                    "Transactional integration event publication requires IIntegrationEventTransactionCoordinator before saving changes.");
            case IntegrationEventPublicationMode.PostCommit:
            case IntegrationEventPublicationMode.Transactional:
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported integration event publication mode '{_integrationEventPublicationPolicy.Mode}'.");
        }
    }

    private static void PreflightLocalDomainEvents(
        IReadOnlyCollection<PendingDomainEvent> pendingEvents,
        IReadOnlyCollection<PendingDomainEvent> integrationEvents)
    {
        if (pendingEvents.Count == integrationEvents.Count)
        {
            return;
        }

        var unsupportedEvent = pendingEvents
            .First(pendingEvent => !IntegrationEventDescriptorFactory.IsIntegrationEvent(pendingEvent.Payload));
        throw new InvalidOperationException(
            $"Local domain event '{unsupportedEvent.Payload.GetType().FullName}' has no dispatch pipeline. "
            + "Remove the unused event or model it as an explicit integration event before saving changes.");
    }

    private List<PendingDomainEvent> CapturePendingEvents()
    {
        var pendingEvents = new List<PendingDomainEvent>();

        foreach (var entity in ChangeTracker
                     .Entries()
                     .Select(entry => entry.Entity)
                     .OfType<IHasDomainEvents>())
        {
            foreach (var domainEvent in entity.DomainEvents.ToArray())
            {
                pendingEvents.Add(new PendingDomainEvent(
                    domainEvent,
                    () => entity.RemoveDomainEvent(domainEvent)));
            }
        }

        foreach (var entity in ChangeTracker
                     .Entries<NetDevPack.Domain.Entity>()
                     .Select(entry => entry.Entity))
        {
            foreach (var domainEvent in entity.DomainEvents?.ToArray() ?? [])
            {
                pendingEvents.Add(new PendingDomainEvent(
                    domainEvent,
                    () => entity.RemoveDomainEvent(domainEvent)));
            }
        }

        return pendingEvents;
    }

    private static void RemoveEvents(IEnumerable<PendingDomainEvent> pendingEvents)
    {
        foreach (var pendingEvent in pendingEvents)
        {
            pendingEvent.Remove();
        }
    }

    private static string CurrentCorrelationId()
    {
        return Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
    }

    private sealed record PendingDomainEvent(object Payload, Action Remove);
}
