using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Infrastructure.Persistence;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionUnitMaterialLifecycleApiTests :
    IClassFixture<OpenLineOpsApiWebApplicationFactory>,
    IDisposable
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ProductionUnitMaterialLifecycleApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenLineOps:Runtime:Persistence:Provider"] =
                        RuntimeSessionPersistenceProviders.InMemory,
                    ["OpenLineOps:Runtime:Coordination:Provider"] =
                        ProductionCoordinationPersistenceProviders.InMemory,
                    ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
                    ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess",
                    ["OpenLineOps:Traceability:Persistence:Provider"] =
                        TraceRecordPersistenceProviders.InMemory,
                    ["OpenLineOps:Traceability:ProjectionRebuild:Enabled"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProductionMaterialArrivalAuthorizer>();
                services.AddSingleton<IProductionMaterialArrivalAuthorizer,
                    AllowingMaterialArrivalAuthorizer>();
            });
        });
        _client = _factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task PostTerminalUnloadAppearsInProductLifecycleWithoutMutatingRunTrace()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var productionUnitId = Guid.NewGuid();
        var productionRunId = Guid.NewGuid();
        var lineId = $"line-lifecycle-{suffix}";
        var stationSystemId = $"station-lifecycle-{suffix}";
        const string slotId = "slot-lifecycle";
        await RegisterProductionUnitAsync(productionUnitId, suffix);
        await RegisterSlotAsync(lineId, stationSystemId, slotId);
        await ArriveUnitAsync(
            productionUnitId,
            lineId,
            stationSystemId,
            BaseTimeUtc.AddSeconds(1));
        await PostSlotCommandAsync(
            lineId,
            stationSystemId,
            slotId,
            "Reserve",
            "ProductionUnit",
            productionUnitId.ToString("D"),
            null,
            BaseTimeUtc.AddSeconds(2));
        await PostSlotCommandAsync(
            lineId,
            stationSystemId,
            slotId,
            "Load",
            "ProductionUnit",
            productionUnitId.ToString("D"),
            null,
            BaseTimeUtc.AddSeconds(3));
        await PostSlotCommandAsync(
            lineId,
            stationSystemId,
            slotId,
            "Start",
            "ProductionUnit",
            productionUnitId.ToString("D"),
            null,
            BaseTimeUtc.AddSeconds(4));
        await PostSlotCommandAsync(
            lineId,
            stationSystemId,
            slotId,
            "Complete",
            "ProductionUnit",
            productionUnitId.ToString("D"),
            null,
            BaseTimeUtc.AddSeconds(5));

        var traceRequest = await CreateFrozenTraceRequestAsync(
            productionRunId,
            productionUnitId,
            $"BOARD-{suffix}",
            BaseTimeUtc.AddSeconds(5));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var traceService = scope.ServiceProvider.GetRequiredService<ITraceRecordService>();
            var projected = await traceService.CreateAsync(traceRequest);
            Assert.True(projected.IsSuccess, projected.Error.Message);
        }

        var traceRoute = $"/api/traceability/records/{productionRunId:D}";
        using var frozenTraceResponse = await _client.GetAsync(traceRoute);
        var frozenTrace = await frozenTraceResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, frozenTraceResponse.StatusCode);
        var frozenTraceSha256 = SHA256.HashData(frozenTrace);

        await PostSlotCommandAsync(
            lineId,
            stationSystemId,
            slotId,
            "Unload",
            "ProductionUnit",
            productionUnitId.ToString("D"),
            StationQueueLocation(lineId, stationSystemId),
            BaseTimeUtc.AddSeconds(6));

        using var lifecycleResponse = await _client.GetAsync(
            LifecycleRoute(productionUnitId));
        using var lifecycle = await ReadJsonAsync(lifecycleResponse);
        Assert.Equal(HttpStatusCode.OK, lifecycleResponse.StatusCode);
        Assert.Equal(
            BaseTimeUtc.AddSeconds(6),
            lifecycle.RootElement.GetProperty("observedThroughUtc").GetDateTimeOffset());
        AssertLocation(
            lifecycle.RootElement.GetProperty("currentLocation"),
            "StationQueue",
            lineId,
            stationSystemId);
        Assert.Equal(
            JsonValueKind.Null,
            lifecycle.RootElement.GetProperty("currentCarrierLocation").ValueKind);
        var lifecycleLocations = lifecycle.RootElement
            .GetProperty("materialLocationTransitions")
            .EnumerateArray()
            .ToArray();
        var lifecycleSlots = lifecycle.RootElement
            .GetProperty("slotOccupancyTransitions")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(3, lifecycleLocations.Length);
        Assert.Equal(5, lifecycleSlots.Length);
        Assert.Contains(
            lifecycleLocations,
            transition => transition.GetProperty("occurredAtUtc").GetDateTimeOffset()
                == BaseTimeUtc.AddSeconds(6));
        Assert.Contains(
            lifecycleSlots,
            transition => transition.GetProperty("currentStatus").GetString() == "Available"
                && transition.GetProperty("occurredAtUtc").GetDateTimeOffset()
                    == BaseTimeUtc.AddSeconds(6));

        using var unchangedTraceResponse = await _client.GetAsync(traceRoute);
        var unchangedTrace = await unchangedTraceResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, unchangedTraceResponse.StatusCode);
        Assert.Equal(frozenTrace, unchangedTrace);
        Assert.Equal(frozenTraceSha256, SHA256.HashData(unchangedTrace));
        using var unchangedTraceJson = JsonDocument.Parse(unchangedTrace);
        Assert.DoesNotContain(
            unchangedTraceJson.RootElement
                .GetProperty("materialLocationTransitions")
                .EnumerateArray(),
            transition => transition.GetProperty("occurredAtUtc").GetDateTimeOffset()
                > BaseTimeUtc.AddSeconds(5));
        Assert.DoesNotContain(
            unchangedTraceJson.RootElement
                .GetProperty("slotOccupancyTransitions")
                .EnumerateArray(),
            transition => transition.GetProperty("occurredAtUtc").GetDateTimeOffset()
                > BaseTimeUtc.AddSeconds(5));
    }

    [Fact]
    public async Task CarrierHistoryIsIncludedOnlyInsideProductionUnitMembershipInterval()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var productionUnitId = Guid.NewGuid();
        var carrierId = $"carrier-lifecycle-{suffix}";
        var lineId = $"line-carrier-lifecycle-{suffix}";
        const string stationA = "station-a";
        const string stationB = "station-b";
        const string stationC = "station-c";
        const string slotId = "slot-carrier";
        await RegisterProductionUnitAsync(productionUnitId, suffix);
        await RegisterCarrierAsync(carrierId);
        await RegisterSlotAsync(lineId, stationB, slotId);
        await ArriveCarrierAsync(carrierId, lineId, stationA, BaseTimeUtc.AddSeconds(1));
        await ArriveUnitAsync(productionUnitId, lineId, stationA, BaseTimeUtc.AddSeconds(2));
        await TransferUnitAsync(
            productionUnitId,
            StationQueueLocation(lineId, stationA),
            CarrierPositionLocation(carrierId, "position-01"),
            BaseTimeUtc.AddSeconds(3));
        await TransferCarrierAsync(
            carrierId,
            StationQueueLocation(lineId, stationA),
            StationQueueLocation(lineId, stationB),
            BaseTimeUtc.AddSeconds(4));

        using (var onboardResponse = await _client.GetAsync(LifecycleRoute(productionUnitId)))
        using (var onboardLifecycle = await ReadJsonAsync(onboardResponse))
        {
            Assert.Equal(HttpStatusCode.OK, onboardResponse.StatusCode);
            Assert.Equal(
                BaseTimeUtc.AddSeconds(4),
                onboardLifecycle.RootElement.GetProperty("observedThroughUtc").GetDateTimeOffset());
            var unitLocation = onboardLifecycle.RootElement.GetProperty("currentLocation");
            Assert.Equal("CarrierPosition", unitLocation.GetProperty("kind").GetString());
            Assert.Equal(carrierId, unitLocation.GetProperty("carrierId").GetString());
            AssertLocation(
                onboardLifecycle.RootElement.GetProperty("currentCarrierLocation"),
                "StationQueue",
                lineId,
                stationB);
        }

        await PostSlotCommandAsync(
            lineId,
            stationB,
            slotId,
            "Reserve",
            "Carrier",
            carrierId,
            null,
            BaseTimeUtc.AddSeconds(5));
        await PostSlotCommandAsync(
            lineId,
            stationB,
            slotId,
            "Load",
            "Carrier",
            carrierId,
            null,
            BaseTimeUtc.AddSeconds(6));

        using (var slottedResponse = await _client.GetAsync(LifecycleRoute(productionUnitId)))
        using (var slottedLifecycle = await ReadJsonAsync(slottedResponse))
        {
            Assert.Equal(HttpStatusCode.OK, slottedResponse.StatusCode);
            Assert.Equal(
                BaseTimeUtc.AddSeconds(6),
                slottedLifecycle.RootElement.GetProperty("observedThroughUtc").GetDateTimeOffset());
            var carrierLocation = slottedLifecycle.RootElement
                .GetProperty("currentCarrierLocation");
            Assert.Equal("Slot", carrierLocation.GetProperty("kind").GetString());
            Assert.Equal(slotId, carrierLocation.GetProperty("slotId").GetString());
            Assert.Equal(
                2,
                slottedLifecycle.RootElement.GetProperty("slotOccupancyTransitions")
                    .GetArrayLength());
        }

        await TransferUnitAsync(
            productionUnitId,
            CarrierPositionLocation(carrierId, "position-01"),
            StationQueueLocation(lineId, stationB),
            BaseTimeUtc.AddSeconds(7));
        await PostSlotCommandAsync(
            lineId,
            stationB,
            slotId,
            "Unload",
            "Carrier",
            carrierId,
            StationQueueLocation(lineId, stationB),
            BaseTimeUtc.AddSeconds(8));
        await TransferCarrierAsync(
            carrierId,
            StationQueueLocation(lineId, stationB),
            StationQueueLocation(lineId, stationC),
            BaseTimeUtc.AddSeconds(9));

        using var response = await _client.GetAsync(LifecycleRoute(productionUnitId));
        using var lifecycle = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            BaseTimeUtc.AddSeconds(7),
            lifecycle.RootElement.GetProperty("observedThroughUtc").GetDateTimeOffset());
        var carrierTransitions = lifecycle.RootElement
            .GetProperty("materialLocationTransitions")
            .EnumerateArray()
            .Where(transition => transition.GetProperty("materialKind").GetString() == "Carrier")
            .ToArray();
        Assert.Equal(2, carrierTransitions.Length);
        Assert.All(
            carrierTransitions,
            transition => Assert.Equal(
                carrierId,
                transition.GetProperty("materialId").GetString()));
        Assert.Equal(
            stationB,
            carrierTransitions[0].GetProperty("destination")
                .GetProperty("stationSystemId")
                .GetString());
        Assert.Equal(
            "Slot",
            carrierTransitions[1].GetProperty("destination")
                .GetProperty("kind")
                .GetString());
        Assert.Equal(
            [BaseTimeUtc.AddSeconds(4), BaseTimeUtc.AddSeconds(6)],
            carrierTransitions
                .Select(transition => transition.GetProperty("occurredAtUtc").GetDateTimeOffset())
                .ToArray());
        var carrierSlotTransitions = lifecycle.RootElement
            .GetProperty("slotOccupancyTransitions")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, carrierSlotTransitions.Length);
        Assert.All(
            carrierSlotTransitions,
            transition => Assert.Equal(
                carrierId,
                transition.GetProperty("materialId").GetString()));
    }

    [Fact]
    public async Task ProductLifecycleEndpointRequiresOperatorRole()
    {
        var route = LifecycleRoute(Guid.NewGuid());
        using var operatorClient = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);
        using var operatorResponse = await operatorClient.GetAsync(route);
        Assert.Equal(HttpStatusCode.NotFound, operatorResponse.StatusCode);

        foreach (var token in new[]
                 {
                     ApiTestAuthentication.EngineeringToken,
                     ApiTestAuthentication.SafetyToken,
                     ApiTestAuthentication.StationAgentToken
                 })
        {
            using var forbiddenClient = _factory.CreateAuthenticatedClient(token: token);
            using var forbiddenResponse = await forbiddenClient.GetAsync(route);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        }

        using var anonymousClient = _factory.CreateClient();
        using var anonymousResponse = await anonymousClient.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<CreateTraceRecordRequest> CreateFrozenTraceRequestAsync(
        Guid productionRunId,
        Guid productionUnitId,
        string identityValue,
        DateTimeOffset completedAtUtc)
    {
        var template = TraceRecordsApiTests.CreateTraceRequest(
            productionRunId,
            identityValue,
            $"lot-unused-{Guid.NewGuid():N}",
            completedAtUtc);
        var templateJson = JsonSerializer.Serialize(template, template.GetType(), JsonOptions);
        var request = JsonSerializer.Deserialize<CreateTraceRecordRequest>(templateJson, JsonOptions)
            ?? throw new InvalidOperationException("Trace fixture could not be read.");
        var repository = _factory.Services.GetRequiredService<IProductionMaterialRepository>();
        var timeline = await repository.ListTimelineAsync(
            ProductionMaterialTimelineQuery.UnionScope(
                productionUnitId: new OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId(
                    productionUnitId),
                throughUtc: completedAtUtc));
        return request with
        {
            ProductionUnitId = productionUnitId,
            ProductModelId = "product.board",
            ProductionUnitIdentityInputKey = "serialNumber",
            ProductionUnitIdentityValue = identityValue,
            LotId = null,
            CarrierId = null,
            Genealogy = timeline
                .Where(static entry => entry.Kind == ProductionMaterialEvidenceKind.Genealogy)
                .Select(ToGenealogyRequest)
                .ToArray(),
            MaterialLocationTransitions = timeline
                .Where(static entry => entry.Kind == ProductionMaterialEvidenceKind.LocationTransition)
                .Select(ToLocationTransitionRequest)
                .ToArray(),
            SlotOccupancyTransitions = timeline
                .Where(static entry => entry.Kind == ProductionMaterialEvidenceKind.SlotOccupancyTransition)
                .Select(ToSlotOccupancyTransitionRequest)
                .ToArray(),
            DispositionTransitions = timeline
                .Where(static entry => entry.Kind == ProductionMaterialEvidenceKind.DispositionTransition)
                .Select(ToDispositionTransitionRequest)
                .ToArray()
        };
    }

    private async Task RegisterProductionUnitAsync(Guid productionUnitId, string suffix)
    {
        using var response = await _client.PostAsJsonAsync("/api/production-units", new
        {
            productionUnitId,
            productModelId = "product.board",
            identityKey = "serialNumber",
            identityValue = $"BOARD-{suffix}",
            lotId = (string?)null,
            occurredAtUtc = BaseTimeUtc
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task RegisterCarrierAsync(string carrierId)
    {
        using var response = await _client.PostAsJsonAsync("/api/production-carriers", new
        {
            carrierId,
            carrierTypeId = "carrier-type.lifecycle",
            capacity = 8,
            occurredAtUtc = BaseTimeUtc
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task RegisterSlotAsync(
        string lineId,
        string stationSystemId,
        string slotId)
    {
        using var response = await _client.PostAsJsonAsync("/api/slot-occupancies", new
        {
            lineId,
            stationSystemId,
            slotId,
            occurredAtUtc = BaseTimeUtc
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private Task ArriveUnitAsync(
        Guid productionUnitId,
        string lineId,
        string stationSystemId,
        DateTimeOffset occurredAtUtc) =>
        PostArrivalAsync(
            $"/api/production-units/{productionUnitId:D}/arrivals",
            lineId,
            stationSystemId,
            occurredAtUtc);

    private Task ArriveCarrierAsync(
        string carrierId,
        string lineId,
        string stationSystemId,
        DateTimeOffset occurredAtUtc) =>
        PostArrivalAsync(
            $"/api/production-carriers/{carrierId}/arrivals",
            lineId,
            stationSystemId,
            occurredAtUtc);

    private async Task PostArrivalAsync(
        string route,
        string lineId,
        string stationSystemId,
        DateTimeOffset occurredAtUtc)
    {
        using var response = await _client.PostAsJsonAsync(route, new
        {
            projectId = "project.lifecycle",
            applicationId = "application.lifecycle",
            projectSnapshotId = "snapshot.lifecycle",
            packageContentSha256 = new string('a', 64),
            stationId = stationSystemId,
            lineId,
            stationSystemId,
            occurredAtUtc
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task TransferUnitAsync(
        Guid productionUnitId,
        object expectedLocation,
        object destination,
        DateTimeOffset occurredAtUtc) =>
        PostTransferAsync(
            $"/api/production-units/{productionUnitId:D}/transfers",
            expectedLocation,
            destination,
            occurredAtUtc);

    private Task TransferCarrierAsync(
        string carrierId,
        object expectedLocation,
        object destination,
        DateTimeOffset occurredAtUtc) =>
        PostTransferAsync(
            $"/api/production-carriers/{carrierId}/transfers",
            expectedLocation,
            destination,
            occurredAtUtc);

    private async Task PostTransferAsync(
        string route,
        object expectedLocation,
        object destination,
        DateTimeOffset occurredAtUtc)
    {
        using var response = await _client.PostAsJsonAsync(route, new
        {
            expectedLocation,
            destination,
            occurredAtUtc
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task PostSlotCommandAsync(
        string lineId,
        string stationSystemId,
        string slotId,
        string command,
        string materialKind,
        string materialId,
        object? destination,
        DateTimeOffset occurredAtUtc)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/slot-occupancies/{lineId}/{stationSystemId}/{slotId}/commands/{command}",
            new
            {
                materialKind,
                materialId,
                destination,
                occurredAtUtc
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static CreateTraceMaterialGenealogyRequest ToGenealogyRequest(
        ProductionMaterialTimelineEntry entry)
    {
        var link = Assert.IsType<MaterialGenealogyLink>(entry.Genealogy);
        return new CreateTraceMaterialGenealogyRequest(
            link.Id.Value,
            link.ParentUnitId.Value,
            link.ChildUnitId.Value,
            link.Relationship,
            link.OperationId,
            link.LinkedBy,
            link.LinkedAtUtc);
    }

    private static CreateTraceMaterialLocationTransitionRequest ToLocationTransitionRequest(
        ProductionMaterialTimelineEntry entry)
    {
        var material = Assert.IsType<MaterialReference>(entry.Material);
        var destination = Assert.IsType<MaterialLocation>(entry.DestinationLocation);
        return new CreateTraceMaterialLocationTransitionRequest(
            entry.EvidenceId,
            entry.ProductionRunId?.Value,
            material.Kind.ToString(),
            material.Value,
            entry.SourceLocation is null ? null : ToLocationRequest(entry.SourceLocation),
            ToLocationRequest(destination),
            entry.ActorId,
            entry.OccurredAtUtc);
    }

    private static CreateTraceSlotOccupancyTransitionRequest ToSlotOccupancyTransitionRequest(
        ProductionMaterialTimelineEntry entry)
    {
        var slot = Assert.IsType<SlotAddress>(entry.Slot);
        return new CreateTraceSlotOccupancyTransitionRequest(
            entry.EvidenceId,
            entry.ProductionRunId?.Value,
            slot.LineId,
            slot.StationSystemId,
            slot.SlotId,
            entry.Material?.Kind.ToString(),
            entry.Material?.Value,
            entry.PreviousSlotStatus?.ToString(),
            entry.CurrentSlotStatus?.ToString(),
            entry.ActorId,
            entry.OccurredAtUtc);
    }

    private static CreateTraceDispositionTransitionRequest ToDispositionTransitionRequest(
        ProductionMaterialTimelineEntry entry)
    {
        var productionUnitId = Assert.IsType<
            OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId>(entry.ProductionUnitId);
        return new CreateTraceDispositionTransitionRequest(
            entry.EvidenceId,
            productionUnitId.Value,
            entry.ProductionRunId?.Value,
            entry.PreviousDisposition?.ToString(),
            entry.CurrentDisposition?.ToString(),
            entry.Reason,
            entry.ActorId,
            entry.OccurredAtUtc);
    }

    private static CreateTraceMaterialLocationRequest ToLocationRequest(MaterialLocation location) =>
        new(
            location.Kind.ToString(),
            location.LineId,
            location.StationSystemId,
            location.SlotId,
            location.CarrierId?.Value,
            location.CarrierPositionId);

    private static object StationQueueLocation(string lineId, string stationSystemId) => new
    {
        kind = "StationQueue",
        lineId,
        stationSystemId,
        slotId = (string?)null,
        carrierId = (string?)null,
        carrierPositionId = (string?)null
    };

    private static object CarrierPositionLocation(string carrierId, string positionId) => new
    {
        kind = "CarrierPosition",
        lineId = (string?)null,
        stationSystemId = (string?)null,
        slotId = (string?)null,
        carrierId,
        carrierPositionId = positionId
    };

    private static string LifecycleRoute(Guid productionUnitId) =>
        $"/api/traceability/production-units/{productionUnitId:D}/material-lifecycle";

    private static void AssertLocation(
        JsonElement location,
        string kind,
        string lineId,
        string stationSystemId)
    {
        Assert.Equal(kind, location.GetProperty("kind").GetString());
        Assert.Equal(lineId, location.GetProperty("lineId").GetString());
        Assert.Equal(stationSystemId, location.GetProperty("stationSystemId").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

    private sealed class AllowingMaterialArrivalAuthorizer :
        IProductionMaterialArrivalAuthorizer
    {
        public ValueTask AuthorizeAsync(
            MaterialArrived message,
            ProductionMaterialArrivalOrigin origin,
            CancellationToken cancellationToken = default)
        {
            StationMessageContract.Validate(message);
            Assert.Equal(ProductionMaterialArrivalOrigin.CoordinatorApi, origin);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
