using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionMaterialsApiTests :
    IClassFixture<WebApplicationFactory<Program>>,
    IDisposable
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ProductionMaterialsApiTests(WebApplicationFactory<Program> factory)
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
                    ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProductionMaterialArrivalAuthorizer>();
                services.AddSingleton<IProductionMaterialArrivalAuthorizer,
                    AllowingApiMaterialArrivalAuthorizer>();
            });
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task ProductionUnitAndLotPostGetPersistCanonicalContract()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var lotId = $"lot-api-{suffix}";
        var productionUnitId = Guid.NewGuid();

        using var lotPost = await _client.PostAsJsonAsync("/api/production-lots", new
        {
            lotId,
            productModelId = "product.board",
            declaredQuantity = 24,
            actorId = "operator.material-api",
            occurredAtUtc = BaseTimeUtc
        });
        using var lotPostBody = await ReadJsonAsync(lotPost);

        Assert.Equal(HttpStatusCode.Created, lotPost.StatusCode);
        Assert.Equal($"/api/production-lots/{lotId}", lotPost.Headers.Location?.OriginalString);
        Assert.Equal(lotId, lotPostBody.RootElement.GetProperty("lotId").GetString());
        Assert.Equal("product.board", lotPostBody.RootElement.GetProperty("productModelId").GetString());
        Assert.Equal(24, lotPostBody.RootElement.GetProperty("declaredQuantity").GetInt32());

        using var unitPost = await _client.PostAsJsonAsync("/api/production-units", new
        {
            productionUnitId,
            productModelId = "product.board",
            identityKey = "serialNumber",
            identityValue = $"BOARD-{suffix}",
            lotId,
            actorId = "operator.material-api",
            occurredAtUtc = BaseTimeUtc.AddSeconds(1)
        });
        using var unitPostBody = await ReadJsonAsync(unitPost);

        Assert.Equal(HttpStatusCode.Created, unitPost.StatusCode);
        Assert.Equal(
            $"/api/production-units/{productionUnitId:D}",
            unitPost.Headers.Location?.OriginalString);
        Assert.Equal(productionUnitId, unitPostBody.RootElement
            .GetProperty("productionUnitId").GetGuid());
        Assert.Equal("serialNumber", unitPostBody.RootElement.GetProperty("identityKey").GetString());
        Assert.Equal(lotId, unitPostBody.RootElement.GetProperty("lotId").GetString());
        Assert.Equal("InProcess", unitPostBody.RootElement.GetProperty("disposition").GetString());
        Assert.Equal(JsonValueKind.Null, unitPostBody.RootElement.GetProperty("location").ValueKind);

        using var lotGet = await _client.GetAsync($"/api/production-lots/{lotId}");
        using var lotGetBody = await ReadJsonAsync(lotGet);
        using var unitGet = await _client.GetAsync($"/api/production-units/{productionUnitId:D}");
        using var unitGetBody = await ReadJsonAsync(unitGet);

        Assert.Equal(HttpStatusCode.OK, lotGet.StatusCode);
        Assert.Equal(24, lotGetBody.RootElement.GetProperty("declaredQuantity").GetInt32());
        Assert.Equal(HttpStatusCode.OK, unitGet.StatusCode);
        Assert.Equal(
            $"BOARD-{suffix}",
            unitGetBody.RootElement.GetProperty("identityValue").GetString());
    }

    [Fact]
    public async Task ArrivalAndSlotCommandsPersistExactOccupancyAndLocationTokens()
    {
        var productionUnitId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var lineId = $"line-{suffix}";
        var stationSystemId = $"station-{suffix}";
        var slotId = "slot-01";
        await RegisterProductionUnitAsync(productionUnitId, suffix);
        await RegisterSlotAsync(lineId, stationSystemId, slotId);

        using var arrival = await _client.PostAsJsonAsync(
            $"/api/production-units/{productionUnitId:D}/arrivals",
            new
            {
                projectId = "project.material-api",
                applicationId = "application.material-api",
                projectSnapshotId = "snapshot.material-api",
                packageContentSha256 = new string('a', 64),
                stationId = stationSystemId,
                lineId,
                stationSystemId,
                actorId = "scanner.material-api",
                occurredAtUtc = BaseTimeUtc.AddSeconds(1)
            });
        using var arrivalBody = await ReadJsonAsync(arrival);

        Assert.Equal(HttpStatusCode.OK, arrival.StatusCode);
        AssertLocation(
            arrivalBody.RootElement.GetProperty("location"),
            "StationQueue",
            lineId,
            stationSystemId);

        using var reserve = await PostSlotCommandAsync(
            lineId,
            stationSystemId,
            slotId,
            "Reserve",
            productionUnitId,
            BaseTimeUtc.AddSeconds(2));
        using var reserveBody = await ReadJsonAsync(reserve);
        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        AssertSlot(reserveBody.RootElement, "Reserved", "ProductionUnit", productionUnitId);

        using var load = await PostSlotCommandAsync(
            lineId,
            stationSystemId,
            slotId,
            "Load",
            productionUnitId,
            BaseTimeUtc.AddSeconds(3));
        using var loadBody = await ReadJsonAsync(load);
        Assert.Equal(HttpStatusCode.OK, load.StatusCode);
        AssertSlot(loadBody.RootElement, "Occupied", "ProductionUnit", productionUnitId);

        using var loadedUnit = await _client.GetAsync($"/api/production-units/{productionUnitId:D}");
        using var loadedUnitBody = await ReadJsonAsync(loadedUnit);
        AssertLocation(
            loadedUnitBody.RootElement.GetProperty("location"),
            "Slot",
            lineId,
            stationSystemId,
            slotId);

        using var start = await PostSlotCommandAsync(
            lineId,
            stationSystemId,
            slotId,
            "Start",
            productionUnitId,
            BaseTimeUtc.AddSeconds(4));
        using var startBody = await ReadJsonAsync(start);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        AssertSlot(startBody.RootElement, "Running", "ProductionUnit", productionUnitId);

        using var complete = await PostSlotCommandAsync(
            lineId,
            stationSystemId,
            slotId,
            "Complete",
            productionUnitId,
            BaseTimeUtc.AddSeconds(5));
        using var completeBody = await ReadJsonAsync(complete);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        AssertSlot(completeBody.RootElement, "Occupied", "ProductionUnit", productionUnitId);

        using var unload = await _client.PostAsJsonAsync(
            SlotCommandRoute(lineId, stationSystemId, slotId, "Unload"),
            new
            {
                materialKind = "ProductionUnit",
                materialId = productionUnitId.ToString("D"),
                destination = StationQueueLocation(lineId, stationSystemId),
                actorId = "operator.material-api",
                occurredAtUtc = BaseTimeUtc.AddSeconds(6)
            });
        using var unloadBody = await ReadJsonAsync(unload);

        Assert.Equal(HttpStatusCode.OK, unload.StatusCode);
        AssertSlot(unloadBody.RootElement, "Available", null, null);

        using var slotGet = await _client.GetAsync(
            $"/api/slot-occupancies/{lineId}/{stationSystemId}/{slotId}");
        using var slotGetBody = await ReadJsonAsync(slotGet);
        using var unitGet = await _client.GetAsync($"/api/production-units/{productionUnitId:D}");
        using var unitGetBody = await ReadJsonAsync(unitGet);

        Assert.Equal(HttpStatusCode.OK, slotGet.StatusCode);
        AssertSlot(slotGetBody.RootElement, "Available", null, null);
        AssertLocation(
            unitGetBody.RootElement.GetProperty("location"),
            "StationQueue",
            lineId,
            stationSystemId);
    }

    [Fact]
    public async Task SlotAvailabilityCommandsPersistBlockedAndOfflineLifecycle()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var lineId = $"line-availability-{suffix}";
        var stationSystemId = $"station-availability-{suffix}";
        var slotId = "slot-maintenance";
        await RegisterSlotAsync(lineId, stationSystemId, slotId);

        async Task AssertCommandAsync(
            string command,
            DateTimeOffset occurredAtUtc,
            string expectedStatus,
            string? reason = null)
        {
            using var response = await _client.PostAsJsonAsync(
                SlotCommandRoute(lineId, stationSystemId, slotId, command),
                new
                {
                    materialKind = (string?)null,
                    materialId = (string?)null,
                    destination = (object?)null,
                    reason,
                    actorId = "operator.material-api",
                    occurredAtUtc
                });
            using var body = await ReadJsonAsync(response);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertSlot(body.RootElement, expectedStatus, null, null);
        }

        await AssertCommandAsync(
            "Block",
            BaseTimeUtc.AddSeconds(1),
            "Blocked",
            "Planned fixture maintenance.");
        await AssertCommandAsync("Unblock", BaseTimeUtc.AddSeconds(2), "Available");
        await AssertCommandAsync("SetOffline", BaseTimeUtc.AddSeconds(3), "Offline");
        await AssertCommandAsync("BringOnline", BaseTimeUtc.AddSeconds(4), "Available");

        using var get = await _client.GetAsync(
            $"/api/slot-occupancies/{lineId}/{stationSystemId}/{slotId}");
        using var getBody = await ReadJsonAsync(get);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertSlot(getBody.RootElement, "Available", null, null);
    }

    [Fact]
    public async Task CarrierArrivalAndCarrierPositionTransferPersistCanonicalLocation()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var carrierId = $"carrier-{suffix}";
        var productionUnitId = Guid.NewGuid();
        var lineId = $"line-{suffix}";
        var stationSystemId = $"station-{suffix}";
        await RegisterProductionUnitAsync(productionUnitId, suffix);

        using var carrierPost = await _client.PostAsJsonAsync("/api/production-carriers", new
        {
            carrierId,
            carrierTypeId = "carrier-type.board-tray",
            capacity = 8,
            actorId = "operator.material-api",
            occurredAtUtc = BaseTimeUtc
        });
        Assert.Equal(HttpStatusCode.Created, carrierPost.StatusCode);

        using var carrierArrival = await _client.PostAsJsonAsync(
            $"/api/production-carriers/{carrierId}/arrivals",
            new
            {
                projectId = "project.material-api",
                applicationId = "application.material-api",
                projectSnapshotId = "snapshot.material-api",
                packageContentSha256 = new string('a', 64),
                stationId = stationSystemId,
                lineId,
                stationSystemId,
                actorId = "scanner.material-api",
                occurredAtUtc = BaseTimeUtc.AddSeconds(1)
            });
        using var carrierArrivalBody = await ReadJsonAsync(carrierArrival);
        Assert.Equal(HttpStatusCode.OK, carrierArrival.StatusCode);
        AssertLocation(
            carrierArrivalBody.RootElement.GetProperty("location"),
            "StationQueue",
            lineId,
            stationSystemId);

        using var unitArrival = await _client.PostAsJsonAsync(
            $"/api/production-units/{productionUnitId:D}/arrivals",
            new
            {
                projectId = "project.material-api",
                applicationId = "application.material-api",
                projectSnapshotId = "snapshot.material-api",
                packageContentSha256 = new string('a', 64),
                stationId = stationSystemId,
                lineId,
                stationSystemId,
                actorId = "scanner.material-api",
                occurredAtUtc = BaseTimeUtc.AddSeconds(1)
            });
        Assert.Equal(HttpStatusCode.OK, unitArrival.StatusCode);

        using var transfer = await _client.PostAsJsonAsync(
            $"/api/production-units/{productionUnitId:D}/transfers",
            new
            {
                expectedLocation = StationQueueLocation(lineId, stationSystemId),
                destination = new
                {
                    kind = "CarrierPosition",
                    lineId = (string?)null,
                    stationSystemId = (string?)null,
                    slotId = (string?)null,
                    carrierId,
                    carrierPositionId = "position-01"
                },
                actorId = "operator.material-api",
                occurredAtUtc = BaseTimeUtc.AddSeconds(2)
            });
        using var transferBody = await ReadJsonAsync(transfer);

        Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);
        var location = transferBody.RootElement.GetProperty("location");
        Assert.Equal("CarrierPosition", location.GetProperty("kind").GetString());
        Assert.Equal(carrierId, location.GetProperty("carrierId").GetString());
        Assert.Equal("position-01", location.GetProperty("carrierPositionId").GetString());

        using var carrierGet = await _client.GetAsync($"/api/production-carriers/{carrierId}");
        using var carrierGetBody = await ReadJsonAsync(carrierGet);
        Assert.Equal(HttpStatusCode.OK, carrierGet.StatusCode);
        Assert.Equal(8, carrierGetBody.RootElement.GetProperty("capacity").GetInt32());
    }

    [Fact]
    public async Task GenealogyPostPersistsEvidenceAndRejectsDuplicateRelationship()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        await RegisterProductionUnitAsync(parentId, $"parent-{suffix}");
        await RegisterProductionUnitAsync(childId, $"child-{suffix}");
        var request = new
        {
            linkId,
            parentProductionUnitId = parentId,
            childProductionUnitId = childId,
            relationship = "ComponentOf",
            operationId = "operation.assembly",
            actorId = "operator.material-api",
            occurredAtUtc = BaseTimeUtc.AddSeconds(1)
        };

        using var response = await _client.PostAsJsonAsync("/api/material-genealogy", request);
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            $"/api/material-genealogy/{linkId:D}",
            response.Headers.Location?.OriginalString);
        Assert.Equal(linkId, body.RootElement.GetProperty("linkId").GetGuid());
        Assert.Equal(parentId, body.RootElement.GetProperty("parentProductionUnitId").GetGuid());
        Assert.Equal(childId, body.RootElement.GetProperty("childProductionUnitId").GetGuid());
        Assert.Equal("ComponentOf", body.RootElement.GetProperty("relationship").GetString());
        Assert.Equal("operation.assembly", body.RootElement.GetProperty("operationId").GetString());

        var repository = _factory.Services.GetRequiredService<IProductionMaterialRepository>();
        var persisted = Assert.Single(
            await repository.ListGenealogyLinksAsync(),
            link => link.Id.Value == linkId);
        Assert.Equal(parentId, persisted.ParentUnitId.Value);
        Assert.Equal(childId, persisted.ChildUnitId.Value);
        Assert.Equal("ComponentOf", persisted.Relationship);
        Assert.Equal("operation.assembly", persisted.OperationId);

        using var duplicate = await _client.PostAsJsonAsync(
            "/api/material-genealogy",
            new
            {
                linkId = Guid.NewGuid(),
                parentProductionUnitId = parentId,
                childProductionUnitId = childId,
                relationship = "ComponentOf",
                operationId = "operation.assembly-repeat",
                actorId = "operator.material-api",
                occurredAtUtc = BaseTimeUtc.AddSeconds(2)
            });
        using var duplicateBody = await ReadJsonAsync(duplicate);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(
            "Runtime.ProductionMaterialAlreadyExists",
            duplicateBody.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task MissingMaterialsReturnNotFoundAndDuplicateRegistrationReturnsConflict()
    {
        var productionUnitId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        await RegisterProductionUnitAsync(productionUnitId, suffix);

        using var duplicate = await _client.PostAsJsonAsync("/api/production-units", new
        {
            productionUnitId,
            productModelId = "product.board",
            identityKey = "serialNumber",
            identityValue = $"BOARD-{suffix}",
            lotId = (string?)null,
            actorId = "operator.material-api",
            occurredAtUtc = BaseTimeUtc.AddSeconds(1)
        });
        using var duplicateBody = await ReadJsonAsync(duplicate);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(
            "Runtime.ProductionMaterialAlreadyExists",
            duplicateBody.RootElement.GetProperty("title").GetString());

        var missingId = Guid.NewGuid();
        using var missingGet = await _client.GetAsync($"/api/production-units/{missingId:D}");
        using var missingArrival = await _client.PostAsJsonAsync(
            $"/api/production-units/{missingId:D}/arrivals",
            new
            {
                projectId = "project.material-api",
                applicationId = "application.material-api",
                projectSnapshotId = "snapshot.material-api",
                packageContentSha256 = new string('a', 64),
                stationId = "station.missing",
                lineId = "line.missing",
                stationSystemId = "station.missing",
                actorId = "scanner.material-api",
                occurredAtUtc = BaseTimeUtc
            });
        using var missingArrivalBody = await ReadJsonAsync(missingArrival);
        using var missingLot = await _client.GetAsync($"/api/production-lots/lot-missing-{suffix}");
        using var missingCarrier = await _client.GetAsync(
            $"/api/production-carriers/carrier-missing-{suffix}");
        using var missingSlot = await _client.GetAsync(
            $"/api/slot-occupancies/line-missing/station-missing/slot-missing-{suffix}");

        Assert.Equal(HttpStatusCode.NotFound, missingGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingArrival.StatusCode);
        Assert.Equal(
            "Runtime.ProductionMaterialNotFound",
            missingArrivalBody.RootElement.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missingLot.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingCarrier.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingSlot.StatusCode);
    }

    [Theory]
    [InlineData("reserve", "ProductionUnit")]
    [InlineData("Reserve", "productionUnit")]
    [InlineData("Reserve", "Dut")]
    [InlineData("Occupied", "ProductionUnit")]
    public async Task SlotCommandsRejectNonCanonicalCommandAndMaterialTokens(
        string command,
        string materialKind)
    {
        var productionUnitId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var lineId = $"line-token-{suffix}";
        var stationSystemId = $"station-token-{suffix}";
        var slotId = "slot-token";
        await RegisterProductionUnitAsync(productionUnitId, suffix);
        await RegisterSlotAsync(lineId, stationSystemId, slotId);

        using var response = await _client.PostAsJsonAsync(
            SlotCommandRoute(lineId, stationSystemId, slotId, command),
            new
            {
                materialKind,
                materialId = productionUnitId.ToString("D"),
                destination = (object?)null,
                actorId = "operator.material-api",
                occurredAtUtc = BaseTimeUtc.AddSeconds(1)
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Workstation")]
    [InlineData("stationQueue")]
    [InlineData("Station")]
    public async Task TransfersRejectOldOrNonCanonicalLocationTokens(string locationKind)
    {
        var productionUnitId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var lineId = $"line-location-{suffix}";
        var stationSystemId = $"station-location-{suffix}";
        await RegisterProductionUnitAsync(productionUnitId, suffix);

        using var arrival = await _client.PostAsJsonAsync(
            $"/api/production-units/{productionUnitId:D}/arrivals",
            new
            {
                projectId = "project.material-api",
                applicationId = "application.material-api",
                projectSnapshotId = "snapshot.material-api",
                packageContentSha256 = new string('a', 64),
                stationId = stationSystemId,
                lineId,
                stationSystemId,
                actorId = "scanner.material-api",
                occurredAtUtc = BaseTimeUtc.AddSeconds(1)
            });
        Assert.Equal(HttpStatusCode.OK, arrival.StatusCode);

        using var response = await _client.PostAsJsonAsync(
            $"/api/production-units/{productionUnitId:D}/transfers",
            new
            {
                expectedLocation = new
                {
                    kind = locationKind,
                    lineId,
                    stationSystemId,
                    slotId = (string?)null,
                    carrierId = (string?)null,
                    carrierPositionId = (string?)null
                },
                destination = StationQueueLocation(lineId, $"{stationSystemId}-next"),
                actorId = "operator.material-api",
                occurredAtUtc = BaseTimeUtc.AddSeconds(2)
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("dutId")]
    [InlineData("dutModelId")]
    [InlineData("workstationId")]
    [InlineData("processStageId")]
    public async Task ProductionUnitPostRejectsUnmappedLegacyFields(string legacyField)
    {
        var productionUnitId = Guid.NewGuid();
        var payload = $$"""
            {
              "productionUnitId": "{{productionUnitId:D}}",
              "productModelId": "product.board",
              "identityKey": "serialNumber",
              "identityValue": "BOARD-STRICT-{{productionUnitId:N}}",
              "lotId": null,
              "actorId": "operator.material-api",
              "occurredAtUtc": "2026-07-11T09:00:00+00:00",
              "{{legacyField}}": "removed"
            }
            """;
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync("/api/production-units", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var missing = await _client.GetAsync($"/api/production-units/{productionUnitId:D}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
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
            actorId = "operator.material-api",
            occurredAtUtc = BaseTimeUtc
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task RegisterSlotAsync(string lineId, string stationSystemId, string slotId)
    {
        using var response = await _client.PostAsJsonAsync("/api/slot-occupancies", new
        {
            lineId,
            stationSystemId,
            slotId,
            actorId = "engineer.material-api",
            occurredAtUtc = BaseTimeUtc
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostSlotCommandAsync(
        string lineId,
        string stationSystemId,
        string slotId,
        string command,
        Guid productionUnitId,
        DateTimeOffset occurredAtUtc)
    {
        return _client.PostAsJsonAsync(
            SlotCommandRoute(lineId, stationSystemId, slotId, command),
            new
            {
                materialKind = "ProductionUnit",
                materialId = productionUnitId.ToString("D"),
                destination = (object?)null,
                actorId = "operator.material-api",
                occurredAtUtc
            });
    }

    private static string SlotCommandRoute(
        string lineId,
        string stationSystemId,
        string slotId,
        string command) =>
        $"/api/slot-occupancies/{lineId}/{stationSystemId}/{slotId}/commands/{command}";

    private static object StationQueueLocation(string lineId, string stationSystemId) => new
    {
        kind = "StationQueue",
        lineId,
        stationSystemId,
        slotId = (string?)null,
        carrierId = (string?)null,
        carrierPositionId = (string?)null
    };

    private static void AssertSlot(
        JsonElement slot,
        string status,
        string? materialKind,
        Guid? productionUnitId)
    {
        Assert.Equal(status, slot.GetProperty("status").GetString());
        if (materialKind is null)
        {
            Assert.Equal(JsonValueKind.Null, slot.GetProperty("materialKind").ValueKind);
            Assert.Equal(JsonValueKind.Null, slot.GetProperty("materialId").ValueKind);
            return;
        }

        Assert.Equal(materialKind, slot.GetProperty("materialKind").GetString());
        Assert.Equal(
            productionUnitId?.ToString("D"),
            slot.GetProperty("materialId").GetString());
    }

    private static void AssertLocation(
        JsonElement location,
        string kind,
        string lineId,
        string stationSystemId,
        string? slotId = null)
    {
        Assert.Equal(kind, location.GetProperty("kind").GetString());
        Assert.Equal(lineId, location.GetProperty("lineId").GetString());
        Assert.Equal(stationSystemId, location.GetProperty("stationSystemId").GetString());
        if (slotId is null)
        {
            Assert.Equal(JsonValueKind.Null, location.GetProperty("slotId").ValueKind);
        }
        else
        {
            Assert.Equal(slotId, location.GetProperty("slotId").GetString());
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

    private sealed class AllowingApiMaterialArrivalAuthorizer :
        IProductionMaterialArrivalAuthorizer
    {
        public ValueTask AuthorizeAsync(
            MaterialArrived message,
            ProductionMaterialArrivalOrigin origin,
            CancellationToken cancellationToken = default)
        {
            StationMessageContract.Validate(message);
            Assert.Equal(ProductionMaterialArrivalOrigin.CoordinatorApi, origin);
            Assert.Equal(StationMaterialArrivalSources.Api, message.Source);
            Assert.Equal(StationMaterialArrivalProducers.CoordinatorApi, message.ProducerId);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
