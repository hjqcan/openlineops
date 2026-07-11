using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class StationJobOutboxHostedService(
    IStationJobCoordinationStore store,
    IStationJobOutboxPublisher publisher,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<StationJobOutboxHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Action<ILogger, Guid, Exception?> LogPublishFailure =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(1, "StationJobPublishFailed"),
            "Station job outbox message {MessageId} publication failed.");
    private static readonly Action<ILogger, Guid, string, Exception?> LogQuarantined =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Critical,
            new EventId(2, "StationDispatchQuarantined"),
            "Station dispatch outbox message {MessageId} was quarantined: {Reason}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pending = await store.ListPendingAsync(32, stoppingToken).ConfigureAwait(false);
            foreach (var item in pending)
            {
                try
                {
                    if (!await AuthorizeOrQuarantineAsync(item, stoppingToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    switch (item.Kind)
                    {
                        case nameof(ResourceLeaseChanged):
                            var change = JsonSerializer.Deserialize<ResourceLeaseChanged>(
                                item.PayloadJson,
                                JsonOptions)
                                ?? throw new InvalidDataException(
                                    "Resource lease outbox payload is empty.");
                            if (change.JobId != item.JobId)
                            {
                                throw new InvalidDataException(
                                    "Resource lease outbox Job identity is inconsistent.");
                            }

                            if (!await AuthorizeOrQuarantineAsync(item, stoppingToken)
                                    .ConfigureAwait(false))
                            {
                                continue;
                            }

                            await publisher.PublishAsync(change, stoppingToken).ConfigureAwait(false);
                            break;
                        case nameof(StationJobRequested):
                            var request = JsonSerializer.Deserialize<StationJobRequested>(
                                item.PayloadJson,
                                JsonOptions)
                                ?? throw new InvalidDataException(
                                    "Station job outbox payload is empty.");
                            if (request.JobId != item.JobId)
                            {
                                throw new InvalidDataException(
                                    "Station job outbox Job identity is inconsistent.");
                            }

                            if (!await AuthorizeOrQuarantineAsync(item, stoppingToken)
                                    .ConfigureAwait(false))
                            {
                                continue;
                            }

                            await publisher.PublishAsync(request, stoppingToken).ConfigureAwait(false);
                            break;
                        default:
                            throw new InvalidDataException(
                                $"Unsupported Station dispatch outbox kind '{item.Kind}'.");
                    }

                    await store.MarkPublishedAsync(item.MessageId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is JsonException
                                                    or InvalidDataException
                                                    or ArgumentException)
                {
                    var reason = $"Station dispatch contains permanent invalid evidence: {exception.Message}";
                    await store.QuarantineJobAsync(
                            item.JobId,
                            reason,
                            clock.UtcNow,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    LogQuarantined(logger, item.MessageId, reason, exception);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    await store.RecordPublishFailureAsync(
                            item.MessageId,
                            exception.Message,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    LogPublishFailure(logger, item.MessageId, exception);
                }
            }

            await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> AuthorizeOrQuarantineAsync(
        StationJobOutboxItem item,
        CancellationToken cancellationToken)
    {
        var request = await store.GetDispatchRequestAsync(item.JobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Station dispatch {item.MessageId:D} has no durable Job request.");
        await using var scope = scopeFactory.CreateAsyncScope();
        var authorizer = scope.ServiceProvider
            .GetRequiredService<StationDispatchPublicationAuthorizer>();
        var decision = await authorizer.AuthorizeAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (decision.Allowed)
        {
            return true;
        }

        var reason = decision.RejectionReason
            ?? "Station dispatch publication authorization was rejected.";
        await store.QuarantineJobAsync(
                item.JobId,
                reason,
                clock.UtcNow,
                CancellationToken.None)
            .ConfigureAwait(false);
        LogQuarantined(logger, item.MessageId, reason, null);
        return false;
    }
}
