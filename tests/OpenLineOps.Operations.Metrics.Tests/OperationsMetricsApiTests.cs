using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Operations.Metrics.Api.Controllers;

namespace OpenLineOps.Operations.Metrics.Tests;

public sealed class OperationsMetricsApiTests
{
    [Fact]
    public void ControllersDeclareLeastPrivilegePolicies()
    {
        AssertPolicy(
            typeof(OperationsMetricsEngineeringController),
            OpenLineOpsApiSecurity.EngineeringPolicy);
        AssertPolicy(
            typeof(OperationsMetricsOperatorController),
            OpenLineOpsApiSecurity.OperatorPolicy);
        AssertPolicy(
            typeof(OperationsMetricsStationAgentController),
            OpenLineOpsApiSecurity.StationAgentPolicy);
    }

    [Fact]
    public async Task ProductionEventsBindStationClaimsAndEnforceStrictJson()
    {
        await using var harness =
            await OperationsMetricsApiTestHarness.CreateAsync();
        var request = ProductionEventRequest();
        var denied = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/production-events",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            request,
            stationId: "station-b");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var created = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/production-events",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            request,
            stationId: "station-a");
        var replay = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/production-events",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            request,
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            OperationsMetricsTestData.BaseUtc.AddHours(12),
            body.GetProperty("receivedAtUtc").GetDateTimeOffset());

        var conflict = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/production-events",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            ProductionEventRequest(good: false),
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var unknown = await harness.SendRawJsonAsync(
            HttpMethod.Post,
            "/api/operations/production-events",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            $$"""
              {
                "eventId": "event-unknown",
                "stationId": "station-a",
                "unitId": "unit-unknown",
                "kind": "UnitCompleted",
                "sourceTimestampUtc": "{{OperationsMetricsTestData.BaseUtc.AddHours(9):O}}",
                "occurredAtUtc": "{{OperationsMetricsTestData.BaseUtc.AddHours(9):O}}",
                "firstAttempt": true,
                "good": true,
                "cycleDurationMilliseconds": 1000,
                "schemaVersion": 1,
                "unexpected": true
              }
              """,
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var operatorDenied = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/production-events",
            "operator-a",
            OpenLineOpsApiSecurity.OperatorRole,
            ProductionEventRequest(eventId: "event-operator"));
        Assert.Equal(HttpStatusCode.Forbidden, operatorDenied.StatusCode);
    }

    [Fact]
    public async Task ShiftConfigurationAndOeeUseSeparatedRoles()
    {
        await using var harness =
            await OperationsMetricsApiTestHarness.CreateAsync();
        var shift = new
        {
            shiftId = "shift-day",
            stationId = "station-a",
            name = "Day",
            timeZoneId = "Asia/Shanghai",
            localStartTime = new TimeOnly(8, 0),
            localEndTime = new TimeOnly(16, 0),
            schemaVersion = 1
        };
        var denied = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/shifts",
            "operator-a",
            OpenLineOpsApiSecurity.OperatorRole,
            shift);
        var created = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/shifts",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole,
            shift);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var shiftReplay = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/shifts",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole,
            shift);
        var shiftConflict = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/shifts",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole,
            new
            {
                shiftId = "shift-day",
                stationId = "station-a",
                name = "Changed",
                timeZoneId = "Asia/Shanghai",
                localStartTime = new TimeOnly(8, 0),
                localEndTime = new TimeOnly(16, 0),
                schemaVersion = 1
            });
        Assert.Equal(HttpStatusCode.OK, shiftReplay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, shiftConflict.StatusCode);

        var window = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/shifts/shift-day/windows",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole,
            new
            {
                windowId = "window-day",
                stationId = "station-a",
                startsAtUtc = OperationsMetricsTestData.BaseUtc.AddHours(8),
                endsAtUtc = OperationsMetricsTestData.BaseUtc.AddHours(9),
                targetQuantity = 60,
                idealCycleTimeMilliseconds = 30_000,
                schemaVersion = 1
            });
        Assert.Equal(HttpStatusCode.Created, window.StatusCode);
        var windowReplay = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/shifts/shift-day/windows",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole,
            new
            {
                windowId = "window-day",
                stationId = "station-a",
                startsAtUtc = OperationsMetricsTestData.BaseUtc.AddHours(8),
                endsAtUtc = OperationsMetricsTestData.BaseUtc.AddHours(9),
                targetQuantity = 60,
                idealCycleTimeMilliseconds = 30_000,
                schemaVersion = 1
            });
        Assert.Equal(HttpStatusCode.OK, windowReplay.StatusCode);
        var production = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/production-events",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            ProductionEventRequest(),
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.Created, production.StatusCode);

        var from = Uri.EscapeDataString(
            OperationsMetricsTestData.BaseUtc.AddHours(8).ToString("O"));
        var to = Uri.EscapeDataString(
            OperationsMetricsTestData.BaseUtc.AddHours(9).ToString("O"));
        var oee = await harness.SendAsync(
            HttpMethod.Get,
            $"/api/operations/oee?stationId=station-a&fromUtc={from}&toUtc={to}",
            "operator-a",
            OpenLineOpsApiSecurity.OperatorRole);
        Assert.Equal(HttpStatusCode.OK, oee.StatusCode);
        var report = await oee.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, report.GetProperty("completedCount").GetInt32());
        Assert.Equal(60, report.GetProperty("taktSeconds").GetDouble());
        Assert.False(string.IsNullOrWhiteSpace(
            report
                .GetProperty("definitions")
                .GetProperty("oee")
                .GetString()));
    }

    [Fact]
    public async Task OperatorReasonCannotClearSourceOwnedDowntime()
    {
        await using var harness =
            await OperationsMetricsApiTestHarness.CreateAsync();
        var started = OperationsMetricsTestData.BaseUtc.AddHours(8);
        var opened = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/downtime",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            new
            {
                factId = "fact-open",
                downtimeId = "downtime-1",
                stationId = "station-a",
                sourceId = "plc-line-stop",
                occurredAtUtc = started
            },
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);

        var operatorClear = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/downtime/downtime-1/source-clearance",
            "operator-a",
            OpenLineOpsApiSecurity.OperatorRole,
            new
            {
                factId = "fact-operator-clear",
                stationId = "station-a",
                sourceId = "plc-line-stop",
                occurredAtUtc = started.AddMinutes(10),
                expectedRevision = 1
            });
        Assert.Equal(HttpStatusCode.Forbidden, operatorClear.StatusCode);

        var reason = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/downtime/downtime-1/reason",
            "operator-a",
            OpenLineOpsApiSecurity.OperatorRole,
            new
            {
                factId = "fact-reason",
                reasonCode = "MaterialShortage",
                reasonComment = "Feeder empty",
                occurredAtUtc = started.AddMinutes(5),
                expectedRevision = 1
            });
        Assert.Equal(HttpStatusCode.OK, reason.StatusCode);
        var reasonBody = await reason.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, reasonBody
            .GetProperty("sourceClearedAtUtc")
            .ValueKind);

        var wrongSource = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/downtime/downtime-1/source-clearance",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            new
            {
                factId = "fact-wrong",
                stationId = "station-a",
                sourceId = "hmi",
                occurredAtUtc = started.AddMinutes(10),
                expectedRevision = 2
            },
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.Forbidden, wrongSource.StatusCode);

        var cleared = await harness.SendAsync(
            HttpMethod.Post,
            "/api/operations/downtime/downtime-1/source-clearance",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            new
            {
                factId = "fact-clear",
                stationId = "station-a",
                sourceId = "plc-line-stop",
                occurredAtUtc = started.AddMinutes(10),
                expectedRevision = 2
            },
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var clearedBody = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, clearedBody.GetProperty("revision").GetInt32());
        Assert.Equal(
            started.AddMinutes(10),
            clearedBody
                .GetProperty("sourceClearedAtUtc")
                .GetDateTimeOffset());
    }

    private static object ProductionEventRequest(
        string eventId = "event-1",
        bool good = true) =>
        new
        {
            eventId,
            stationId = "station-a",
            unitId = "unit-1",
            kind = "UnitCompleted",
            sourceTimestampUtc =
                OperationsMetricsTestData.BaseUtc.AddHours(8).AddMinutes(9),
            occurredAtUtc =
                OperationsMetricsTestData.BaseUtc.AddHours(8).AddMinutes(10),
            firstAttempt = true,
            good,
            cycleDurationMilliseconds = 50_000,
            schemaVersion = 1
        };

    private static void AssertPolicy(Type controllerType, string expectedPolicy)
    {
        var attribute = Assert.Single(
            controllerType.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(expectedPolicy, attribute.Policy);
    }
}
