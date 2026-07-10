using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Events;
using OpenLineOps.Operations.Domain.Events.Converters;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Shared.Enums;
using OpenLineOps.Operations.Domain.Shared.IntegrationEvents;

namespace OpenLineOps.Operations.Tests;

public sealed class AlarmTests
{
    [Fact]
    public void RaiseCreatesOpenAlarmAndDomainEvent()
    {
        var alarm = CreateAlarm();

        Assert.Equal(AlarmStatus.Raised, alarm.Status);
        Assert.True(alarm.IsOpen);
        Assert.Equal(AlarmSeverity.Critical, alarm.Severity);
        Assert.IsType<AlarmRaisedDomainEvent>(Assert.Single(alarm.DomainEvents));
    }

    [Fact]
    public void RaisedEventIsMarkedAndConvertedAsIntegrationDto()
    {
        var alarm = CreateAlarm();
        var raisedEvent = Assert.IsType<AlarmRaisedDomainEvent>(Assert.Single(alarm.DomainEvents));
        var registry = new IntegrationDtoConverterRegistry([new AlarmIntegrationDtoConverter()]);

        var descriptor = Assert.Single(IntegrationEventDescriptorFactory.Create([raisedEvent]));
        var payload = Assert.IsType<AlarmRaisedIntegrationDto>(
            registry.ConvertOrOriginal(raisedEvent));

        Assert.IsAssignableFrom<IIntegrationEvent>(raisedEvent);
        Assert.Equal(AlarmRaisedIntegrationDto.EventName, descriptor.EventName);
        Assert.Equal(AlarmRaisedIntegrationDto.Version, descriptor.Version);
        Assert.Equal(alarm.Id.Value, payload.AlarmId);
        Assert.Equal("station-alpha", payload.StationId);
    }

    [Fact]
    public void AcknowledgeTransitionsRaisedAlarm()
    {
        var alarm = CreateAlarm();

        var result = alarm.Acknowledge("operator-a", DateTimeOffset.UtcNow);

        Assert.True(result.Succeeded);
        Assert.Equal(AlarmStatus.Acknowledged, alarm.Status);
        Assert.Equal("operator-a", alarm.AcknowledgedBy);
        Assert.Single(alarm.DomainEvents);
    }

    [Fact]
    public void ResolveClosesAlarm()
    {
        var alarm = CreateAlarm();

        var result = alarm.Resolve("operator-a", "Recovered.", DateTimeOffset.UtcNow);

        Assert.True(result.Succeeded);
        Assert.Equal(AlarmStatus.Resolved, alarm.Status);
        Assert.False(alarm.IsOpen);
        Assert.Equal("Recovered.", alarm.ResolutionNote);
        Assert.Single(alarm.DomainEvents);
    }

    [Fact]
    public void ResolvedAlarmCannotBeAcknowledged()
    {
        var alarm = CreateAlarm();
        alarm.Resolve("operator-a", "Recovered.", DateTimeOffset.UtcNow);

        var result = alarm.Acknowledge("operator-b", DateTimeOffset.UtcNow);

        Assert.False(result.Succeeded);
        Assert.Equal("Operations.Alarm.AlreadyResolved", result.Code);
    }

    private static Alarm CreateAlarm()
    {
        return Alarm.Raise(
            new AlarmId("operations.alarm.domain.v1"),
            "station-alpha",
            "runtime",
            "session-alpha",
            AlarmSeverity.Critical,
            "Runtime failed",
            "Runtime command failed.",
            DateTimeOffset.UtcNow);
    }
}
