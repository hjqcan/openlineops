using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Integration.Api.Controllers;
using OpenLineOps.Integration.Api.Transport;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Application.Queries;
using OpenLineOps.Integration.Domain.Identifiers;

namespace OpenLineOps.Integration.Api.Tests;

public sealed class IntegrationApiTests
{
    [Fact]
    public void ControllersDeclareSeparatedLeastPrivilegePolicies()
    {
        AssertPolicy(
            typeof(IntegrationEngineeringController),
            OpenLineOpsApiSecurity.EngineeringPolicy);
        AssertPolicy(
            typeof(IntegrationOperatorController),
            OpenLineOpsApiSecurity.OperatorPolicy);
        AssertPolicy(
            typeof(IntegrationStationAgentController),
            OpenLineOpsApiSecurity.StationAgentPolicy);
    }

    [Fact]
    public async Task WorkOrderCommandsUseAuthenticatedActorsAndRoleBoundaries()
    {
        await using var harness = await IntegrationApiTestHarness.CreateAsync();
        var create = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/work-orders",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole,
            new
            {
                workOrderId = "order-001",
                productModelId = "model-a",
                targetQuantity = 20,
                factId = "fact-create-001",
                occurredAtUtc = TestData.OccurredAtUtc
            });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using (var document = JsonDocument.Parse(await create.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(
                "engineer-a",
                document.RootElement
                    .GetProperty("facts")[0]
                    .GetProperty("actorId")
                    .GetString());
        }

        var engineeringRead = await harness.SendAsync(
            HttpMethod.Get,
            "/api/integration/work-orders/order-001",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);
        Assert.Equal(HttpStatusCode.Forbidden, engineeringRead.StatusCode);

        var operatorRead = await harness.SendAsync(
            HttpMethod.Get,
            "/api/integration/work-orders/order-001",
            "operator-a",
            OpenLineOpsApiSecurity.OperatorRole);
        Assert.Equal(HttpStatusCode.OK, operatorRead.StatusCode);

        var release = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/work-orders/order-001/transitions",
            "operator-a",
            OpenLineOpsApiSecurity.OperatorRole,
            new
            {
                factId = "fact-release-001",
                kind = "Released",
                occurredAtUtc = TestData.OccurredAtUtc.AddMinutes(1),
                reason = (string?)null
            });
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        using var releaseDocument =
            JsonDocument.Parse(await release.Content.ReadAsStreamAsync());
        Assert.Equal(
            "operator-a",
            releaseDocument.RootElement
                .GetProperty("facts")[1]
                .GetProperty("actorId")
                .GetString());
    }

    [Fact]
    public async Task StationClaimsBindInboundRequestsAndOutboundResponses()
    {
        var handler = new ReadyWorkRequestHandler();
        await using var harness =
            await IntegrationApiTestHarness.CreateAsync(handler);
        var denied = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/work-requests",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            TestData.WorkRequest(stationId: "station-b"),
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var accepted = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/work-requests",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            TestData.WorkRequest(),
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        var ownResponse = await harness.SendAsync(
            HttpMethod.Get,
            "/api/integration/work-responses/response-request-001",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            stationId: "station-a");
        var otherResponse = await harness.SendAsync(
            HttpMethod.Get,
            "/api/integration/work-responses/response-request-001",
            "agent-b",
            OpenLineOpsApiSecurity.StationAgentRole,
            stationId: "station-b");
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherResponse.StatusCode);

        var operatorRequest = await harness.SendAsync(
            HttpMethod.Get,
            "/api/integration/work-requests/request-001",
            "operator-a",
            OpenLineOpsApiSecurity.OperatorRole);
        Assert.Equal(HttpStatusCode.OK, operatorRequest.StatusCode);
        using var requestDocument =
            JsonDocument.Parse(await operatorRequest.Content.ReadAsStreamAsync());
        Assert.Equal(
            "station-a",
            requestDocument.RootElement.GetProperty("sourceSystem").GetString());
    }

    [Fact]
    public async Task InboxReplayIsIdempotentAndChangedContentReturnsConflict()
    {
        var handler = new ReadyWorkRequestHandler();
        await using var harness =
            await IntegrationApiTestHarness.CreateAsync(handler);

        var first = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/work-requests",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            TestData.WorkRequest(),
            stationId: "station-a");
        var replay = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/work-requests",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            TestData.WorkRequest(),
            stationId: "station-a");
        var conflict = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/work-requests",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            TestData.WorkRequest(quantity: 5),
            stationId: "station-a");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1, handler.InvocationCount);
        using var replayDocument =
            JsonDocument.Parse(await replay.Content.ReadAsStreamAsync());
        Assert.Equal(
            "Replayed",
            replayDocument.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task JsonInputRejectsUnknownMembersAndIncorrectPropertyCasing()
    {
        await using var harness = await IntegrationApiTestHarness.CreateAsync();
        var unknownMember = await harness.SendRawJsonAsync(
            HttpMethod.Post,
            "/api/integration/work-orders",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole,
            $$"""
              {
                "workOrderId": "order-unknown",
                "productModelId": "model-a",
                "targetQuantity": 1,
                "factId": "fact-unknown",
                "occurredAtUtc": "{{TestData.OccurredAtUtc:O}}",
                "unexpected": true
              }
              """);
        var incorrectCase = await harness.SendRawJsonAsync(
            HttpMethod.Post,
            "/api/integration/work-orders",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole,
            $$"""
              {
                "WorkOrderId": "order-case",
                "productModelId": "model-a",
                "targetQuantity": 1,
                "factId": "fact-case",
                "occurredAtUtc": "{{TestData.OccurredAtUtc:O}}"
              }
              """);

        Assert.Equal(HttpStatusCode.BadRequest, unknownMember.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, incorrectCase.StatusCode);
    }

    [Fact]
    public async Task UnconfiguredEnterpriseEndpointsFailClosedWithoutInboxWrites()
    {
        await using var harness = await IntegrationApiTestHarness.CreateAsync();
        var response = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/work-requests",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            TestData.WorkRequest(),
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var queryStore = harness.Services.GetRequiredService<IIntegrationQueryStore>();
        Assert.Null(await queryStore.GetWorkRequestAsync(
            new WorkRequestId("request-001")));
        var readiness = Assert.IsAssignableFrom<IWorkRequestHandlerReadiness>(
            harness.Services.GetRequiredService<IWorkRequestHandler>());
        Assert.False(readiness.IsReady);

        var connector =
            harness.Services.GetRequiredService<IIntegrationConnector>();
        var connectorReadiness =
            Assert.IsAssignableFrom<IIntegrationConnectorReadiness>(connector);
        Assert.False(connectorReadiness.IsReady);
        await Assert.ThrowsAsync<IntegrationEndpointUnavailableException>(
            async () => await connector.SendAsync(new IntegrationOutboundMessage(
                1,
                "message-001",
                "correlation-001",
                new string('a', 64),
                "{}",
                TestData.OccurredAtUtc,
                0,
                TestData.OccurredAtUtc)));
    }

    [Fact]
    public async Task DeadLetterReplayIsEngineeringOnlyAndWritesAuditEvidence()
    {
        await using var harness = await IntegrationApiTestHarness.CreateAsync(
            new ReadyWorkRequestHandler());
        var submitted = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/work-requests",
            "agent-a",
            OpenLineOpsApiSecurity.StationAgentRole,
            TestData.WorkRequest(requestId: "request-dead"),
            stationId: "station-a");
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);

        var outbox = harness.Services.GetRequiredService<IIntegrationOutboxStore>();
        await outbox.RecordFailureAsync(
            "response-request-dead",
            expectedAttemptCount: 0,
            "enterprise network offline",
            TestData.ReceivedAtUtc,
            TestData.ReceivedAtUtc,
            deadLetter: true);

        var listed = await harness.SendAsync(
            HttpMethod.Get,
            "/api/integration/outbox/dead-letters",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var deadLetters =
            await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, deadLetters.GetArrayLength());

        var denied = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/outbox/response-request-dead/replay",
            "operator-a",
            OpenLineOpsApiSecurity.OperatorRole,
            new { reason = "operator replay" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var replayed = await harness.SendAsync(
            HttpMethod.Post,
            "/api/integration/outbox/response-request-dead/replay",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole,
            new { reason = "enterprise link restored" });
        Assert.Equal(HttpStatusCode.NoContent, replayed.StatusCode);

        var audit = await harness.SendAsync(
            HttpMethod.Get,
            "/api/integration/outbox/response-request-dead/replay-audit",
            "engineer-a",
            OpenLineOpsApiSecurity.EngineeringRole);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var auditItems = await audit.Content.ReadFromJsonAsync<JsonElement>();
        var auditItem = Assert.Single(auditItems.EnumerateArray());
        Assert.Equal(
            "engineer-a",
            auditItem.GetProperty("actorId").GetString());
        Assert.Equal(
            "enterprise link restored",
            auditItem.GetProperty("reason").GetString());
        Assert.Equal(1, auditItem.GetProperty("previousAttemptCount").GetInt32());
    }

    private static void AssertPolicy(Type controllerType, string expectedPolicy)
    {
        var attribute = Assert.Single(
            controllerType.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(expectedPolicy, attribute.Policy);
    }
}
