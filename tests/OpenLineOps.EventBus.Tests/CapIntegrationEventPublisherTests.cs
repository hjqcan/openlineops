using DotNetCore.CAP;
using Microsoft.Extensions.Logging.Abstractions;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.EventBus.Cap;
using OpenLineOps.Operations.Domain.Events;
using OpenLineOps.Operations.Domain.Events.Converters;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Shared.Enums;
using OpenLineOps.Operations.Domain.Shared.IntegrationEvents;

namespace OpenLineOps.EventBus.Tests;

public sealed class CapIntegrationEventPublisherTests
{
    [Fact]
    public async Task PublishAsyncConvertsDomainEventAndPublishesToCapTopic()
    {
        var capPublisher = new CapturingCapPublisher();
        var registry = new IntegrationDtoConverterRegistry([new AlarmIntegrationDtoConverter()]);
        var publisher = new CapIntegrationEventPublisher(
            capPublisher,
            registry,
            NullLogger<CapIntegrationEventPublisher>.Instance);
        var raisedAt = DateTimeOffset.Parse("2026-06-30T10:15:30Z", null, System.Globalization.DateTimeStyles.AssumeUniversal);
        var domainEvent = new AlarmRaisedDomainEvent(
            new AlarmId("alarm-alpha"),
            "station-alpha",
            "runtime",
            "session-alpha",
            AlarmSeverity.Critical,
            "Runtime failed",
            "Command failed.",
            raisedAt);

        await publisher.PublishAsync([domainEvent]);

        var message = Assert.Single(capPublisher.Messages);
        Assert.Equal(AlarmRaisedIntegrationDto.EventName, message.Name);
        var payload = Assert.IsType<AlarmRaisedIntegrationDto>(message.Content);
        Assert.Equal("alarm-alpha", payload.AlarmId);
        Assert.Equal("station-alpha", payload.StationId);
        Assert.Equal(AlarmSeverity.Critical, payload.Severity);
        Assert.Equal(AlarmRaisedIntegrationDto.Version, message.Headers["event-version"]);
        Assert.Equal("alarm-alpha", message.Headers["aggregate-id"]);
        Assert.Equal(typeof(AlarmRaisedDomainEvent).FullName, message.Headers["event-type"]);
        Assert.False(string.IsNullOrWhiteSpace(message.Headers["correlation-id"]));
        Assert.False(string.IsNullOrWhiteSpace(message.Headers["event-timestamp"]));
    }

    [Fact]
    public async Task PublishAsyncSkipsNonIntegrationDomainEvents()
    {
        var capPublisher = new CapturingCapPublisher();
        var publisher = new CapIntegrationEventPublisher(
            capPublisher,
            new IntegrationDtoConverterRegistry(),
            NullLogger<CapIntegrationEventPublisher>.Instance);

        await publisher.PublishAsync([new object()]);

        Assert.Empty(capPublisher.Messages);
    }

    [Fact]
    public async Task PublishAsyncThrowsWhenCapPublishFails()
    {
        var capPublisher = new CapturingCapPublisher
        {
            PublishException = new InvalidOperationException("CAP unavailable.")
        };
        var publisher = CreatePublisher(capPublisher);
        var domainEvent = CreateAlarmRaisedDomainEvent();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.PublishAsync([domainEvent]));

        Assert.Empty(capPublisher.Messages);
    }

    [Fact]
    public async Task PublishTransactionalAsyncThrowsWhenCapPublishFails()
    {
        var capPublisher = new CapturingCapPublisher
        {
            PublishException = new InvalidOperationException("CAP unavailable.")
        };
        var publisher = CreatePublisher(capPublisher);
        var domainEvent = CreateAlarmRaisedDomainEvent();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.PublishTransactionalAsync([domainEvent]));
    }

    private static CapIntegrationEventPublisher CreatePublisher(CapturingCapPublisher capPublisher)
    {
        var registry = new IntegrationDtoConverterRegistry([new AlarmIntegrationDtoConverter()]);
        return new CapIntegrationEventPublisher(
            capPublisher,
            registry,
            NullLogger<CapIntegrationEventPublisher>.Instance);
    }

    private static AlarmRaisedDomainEvent CreateAlarmRaisedDomainEvent()
    {
        return new AlarmRaisedDomainEvent(
            new AlarmId("alarm-alpha"),
            "station-alpha",
            "runtime",
            "session-alpha",
            AlarmSeverity.Critical,
            "Runtime failed",
            "Command failed.",
            DateTimeOffset.Parse("2026-06-30T10:15:30Z", null, System.Globalization.DateTimeStyles.AssumeUniversal));
    }

    private sealed class CapturingCapPublisher : ICapPublisher
    {
        private readonly List<PublishedMessage> _messages = [];

        public IServiceProvider ServiceProvider => EmptyServiceProvider.Instance;

        public ICapTransaction? Transaction { get; set; }

        public Exception? PublishException { get; init; }

        public IReadOnlyList<PublishedMessage> Messages => _messages;

        public Task PublishAsync<T>(
            string name,
            T? contentObj,
            string? callbackName = null,
            CancellationToken cancellationToken = default)
        {
            _messages.Add(new PublishedMessage(name, contentObj, new Dictionary<string, string?>()));
            return Task.CompletedTask;
        }

        public Task PublishAsync<T>(
            string name,
            T? contentObj,
            IDictionary<string, string?> headers,
            CancellationToken cancellationToken = default)
        {
            if (PublishException is not null)
            {
                throw PublishException;
            }

            _messages.Add(new PublishedMessage(name, contentObj, new Dictionary<string, string?>(headers)));
            return Task.CompletedTask;
        }

        public void Publish<T>(
            string name,
            T? contentObj,
            string? callbackName = null)
        {
            _messages.Add(new PublishedMessage(name, contentObj, new Dictionary<string, string?>()));
        }

        public void Publish<T>(
            string name,
            T? contentObj,
            IDictionary<string, string?> headers)
        {
            _messages.Add(new PublishedMessage(name, contentObj, new Dictionary<string, string?>(headers)));
        }

        public Task PublishDelayAsync<T>(
            TimeSpan delayTime,
            string name,
            T? contentObj,
            IDictionary<string, string?> headers,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task PublishDelayAsync<T>(
            TimeSpan delayTime,
            string name,
            T? contentObj,
            string? callbackName = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void PublishDelay<T>(
            TimeSpan delayTime,
            string name,
            T? contentObj,
            IDictionary<string, string?> headers)
        {
            throw new NotSupportedException();
        }

        public void PublishDelay<T>(
            TimeSpan delayTime,
            string name,
            T? contentObj,
            string? callbackName = null)
        {
            throw new NotSupportedException();
        }
    }

    private sealed record PublishedMessage(
        string Name,
        object? Content,
        IReadOnlyDictionary<string, string?> Headers);

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }
}
