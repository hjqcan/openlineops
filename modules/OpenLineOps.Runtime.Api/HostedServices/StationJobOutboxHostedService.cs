using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Persistence;

namespace OpenLineOps.Runtime.Api.HostedServices;

public sealed class StationJobOutboxHostedService(
    IStationJobCoordinationStore store,
    IStationJobOutboxPublisher publisher,
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pending = await store.ListPendingAsync(32, stoppingToken).ConfigureAwait(false);
            foreach (var item in pending)
            {
                try
                {
                    var request = JsonSerializer.Deserialize<StationJobRequested>(
                        item.PayloadJson,
                        JsonOptions)
                        ?? throw new InvalidDataException("Station job outbox payload is empty.");
                    await publisher.PublishAsync(request, stoppingToken).ConfigureAwait(false);
                    await store.MarkPublishedAsync(item.MessageId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
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
}
