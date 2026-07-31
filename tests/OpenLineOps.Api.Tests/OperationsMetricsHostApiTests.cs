using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenLineOps.Api.Tests;

public sealed class OperationsMetricsHostApiTests(
    OpenLineOpsApiWebApplicationFactory factory)
    : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    [Fact]
    public async Task HostRegistersMetricsAndUsesClaimBoundStationEvents()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var stationId = ApiTestAuthentication.StationAgentStationId;
        var now = DateTimeOffset.UtcNow;
        using var engineering = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.EngineeringToken);
        using var agent = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.StationAgentToken);
        using var operatorClient = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);

        using var shift = await engineering.PostAsJsonAsync(
            "/api/operations/shifts",
            new
            {
                shiftId = $"shift-{suffix}",
                stationId,
                name = "Qualification shift",
                timeZoneId = "UTC",
                localStartTime = new TimeOnly(0, 0),
                localEndTime = new TimeOnly(23, 59),
                schemaVersion = 1
            });
        Assert.Equal(HttpStatusCode.Created, shift.StatusCode);

        using var window = await engineering.PostAsJsonAsync(
            $"/api/operations/shifts/shift-{suffix}/windows",
            new
            {
                windowId = $"window-{suffix}",
                stationId,
                startsAtUtc = now.AddMinutes(-5),
                endsAtUtc = now.AddMinutes(5),
                targetQuantity = 10,
                idealCycleTimeMilliseconds = 30_000,
                schemaVersion = 1
            });
        Assert.Equal(HttpStatusCode.Created, window.StatusCode);

        using var production = await agent.PostAsJsonAsync(
            "/api/operations/production-events",
            new
            {
                eventId = $"event-{suffix}",
                stationId,
                unitId = $"unit-{suffix}",
                kind = "UnitCompleted",
                sourceTimestampUtc = now.AddSeconds(-2),
                occurredAtUtc = now.AddSeconds(-1),
                firstAttempt = true,
                good = true,
                cycleDurationMilliseconds = 25_000,
                schemaVersion = 1
            });
        Assert.Equal(HttpStatusCode.Created, production.StatusCode);

        var from = Uri.EscapeDataString(now.AddMinutes(-5).ToString("O"));
        var to = Uri.EscapeDataString(now.AddMinutes(5).ToString("O"));
        using var oee = await operatorClient.GetAsync(
            $"/api/operations/oee?stationId={stationId}&fromUtc={from}&toUtc={to}");
        using var report = await JsonDocument.ParseAsync(
            await oee.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, oee.StatusCode);
        Assert.Equal(1, report.RootElement.GetProperty("completedCount").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("goodCount").GetInt32());
    }

    [Fact]
    public async Task HostRejectsSpoofedStationAndUnknownMembers()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var agent = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.StationAgentToken);

        using var spoofed = await agent.PostAsJsonAsync(
            "/api/operations/production-events",
            new
            {
                eventId = $"event-spoofed-{suffix}",
                stationId = "station.other",
                unitId = $"unit-{suffix}",
                kind = "UnitCompleted",
                sourceTimestampUtc = DateTimeOffset.UtcNow,
                occurredAtUtc = DateTimeOffset.UtcNow,
                firstAttempt = true,
                good = true,
                cycleDurationMilliseconds = 1_000,
                schemaVersion = 1
            });
        Assert.Equal(HttpStatusCode.Forbidden, spoofed.StatusCode);

        using var unknown = await agent.PostAsJsonAsync(
            "/api/operations/production-events",
            new
            {
                eventId = $"event-unknown-{suffix}",
                stationId = ApiTestAuthentication.StationAgentStationId,
                unitId = $"unit-{suffix}",
                kind = "UnitCompleted",
                sourceTimestampUtc = DateTimeOffset.UtcNow,
                occurredAtUtc = DateTimeOffset.UtcNow,
                firstAttempt = true,
                good = true,
                cycleDurationMilliseconds = 1_000,
                schemaVersion = 1,
                actorId = "spoofed"
            });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }
}
