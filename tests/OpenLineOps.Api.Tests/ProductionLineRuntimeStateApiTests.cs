using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Runtime.Application.Monitoring;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionLineRuntimeStateApiTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

    private readonly OpenLineOpsApiWebApplicationFactory _factory;

    public ProductionLineRuntimeStateApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetLineStateReturnsFormalMaterialStationSlotCarrierAndFencingProjection()
    {
        var state = CreateState();
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProductionLineRuntimeStateReader>();
                services.AddSingleton<IProductionLineRuntimeStateReader>(
                    new StubLineRuntimeStateReader(state));
            });
        });
        using var client = factory.CreateAuthenticatedClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/api/operations/lines/line-api/state");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = body.RootElement;
        Assert.Equal("line-api", root.GetProperty("productionLineDefinitionId").GetString());
        Assert.Equal(1, root.GetProperty("activeRunCount").GetInt32());
        Assert.Single(root.GetProperty("activeRuns").EnumerateArray());
        var productionUnit = Assert.Single(root.GetProperty("productionUnits").EnumerateArray());
        Assert.Equal("InProcess", productionUnit.GetProperty("disposition").GetString());
        Assert.Equal("Unknown", productionUnit.GetProperty("judgement").GetString());
        Assert.Equal(
            "Slot",
            productionUnit.GetProperty("location").GetProperty("kind").GetString());

        var station = Assert.Single(root.GetProperty("stations").EnumerateArray());
        Assert.Equal("Running", station.GetProperty("status").GetString());
        Assert.Equal("agent-api", station.GetProperty("agentId").GetString());
        Assert.Equal("station-host-api", station.GetProperty("stationId").GetString());
        Assert.Equal("Heartbeat", station.GetProperty("agentPresenceState").GetString());
        Assert.Equal("Online", station.GetProperty("agentPresenceHealth").GetString());
        Assert.Equal(0, station.GetProperty("agentPresenceAgeSeconds").GetDouble());
        Assert.Single(station.GetProperty("queue").EnumerateArray());
        var operation = Assert.Single(station.GetProperty("activeOperations").EnumerateArray());
        var resources = operation.GetProperty("resources").EnumerateArray().ToArray();
        Assert.Equal(4, resources.Length);
        Assert.All(resources, resource =>
        {
            Assert.Equal("Leased", resource.GetProperty("status").GetString());
            Assert.True(resource.GetProperty("fencingToken").GetInt64() > 0);
        });

        var slot = Assert.Single(root.GetProperty("slots").EnumerateArray());
        Assert.Equal("Running", slot.GetProperty("status").GetString());
        Assert.Equal("ProductionUnit", slot.GetProperty("materialKind").GetString());
        var carrier = Assert.Single(root.GetProperty("carriers").EnumerateArray());
        Assert.Equal("carrier-api", carrier.GetProperty("carrierId").GetString());
        Assert.Single(carrier.GetProperty("productionUnits").EnumerateArray());
    }

    [Theory]
    [InlineData(" line-api")]
    [InlineData("line-api ")]
    public async Task GetLineStateRejectsNonCanonicalLineIdentity(string lineId)
    {
        using var client = _factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(
            $"/api/operations/lines/{Uri.EscapeDataString(lineId)}/state");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static ProductionLineRuntimeState CreateState()
    {
        var productionUnitId = ProductionUnitId.New();
        var carriedUnitId = ProductionUnitId.New();
        var runId = ProductionRunId.New();
        var resources = new[]
        {
            new ResourceRequirement(ResourceKind.Station, "station-api"),
            new ResourceRequirement(ResourceKind.Slot, "line-api/station-api/slot-api"),
            new ResourceRequirement(ResourceKind.Fixture, "fixture-api"),
            new ResourceRequirement(ResourceKind.Device, "device-api")
        };
        var definition = new OperationRunDefinition(
            "operation-api",
            "station-api",
            new StationId("station-api"),
            new ProcessDefinitionId("process-api"),
            new ProcessVersionId("process-version-api"),
            new ConfigurationSnapshotId("configuration-api"),
            new RecipeSnapshotId("recipe-api"),
            resources);
        var run = ProductionRun.Create(
            runId,
            "project-api",
            "application-api",
            "snapshot-api",
            "topology-api",
            "line-api",
            OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New(),
            new ProductionUnitIdentity("board-api", "serialNumber", "SN-API-STATE"),
            null,
            "carrier-api",
            "operator-api",
            definition.OperationId,
            BaseTimeUtc,
            [definition],
            []);
        Assert.True(run.Start(BaseTimeUtc).Succeeded);
        var operation = Assert.Single(run.Operations);
        var leases = resources.Select((resource, index) => new ResourceLease(
            resource,
            runId,
            operation.OperationRunId,
            index + 1,
            BaseTimeUtc,
            BaseTimeUtc.AddMinutes(10))).ToArray();
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            leases,
            BaseTimeUtc).Succeeded);
        var slotAddress = new SlotAddress("line-api", "station-api", "slot-api");
        var material = MaterialReference.ForProductionUnit(productionUnitId);
        var unitLocation = MaterialLocation.InSlot(slotAddress);
        var carrierLocation = MaterialLocation.AtStation("line-api", "station-api");
        var operationResources = resources.Select((resource, index) =>
            new ProductionLineResourceState(
                resource.Kind,
                resource.ResourceId,
                ProductionLineResourceRuntimeStatus.Leased,
                index + 1,
                BaseTimeUtc,
                BaseTimeUtc.AddMinutes(10))).ToArray();
        return new ProductionLineRuntimeState(
            "line-api",
            BaseTimeUtc.AddSeconds(1),
            [run.ToSnapshot()],
            [
                new ProductionLineProductionUnitState(
                    productionUnitId,
                    "board-api",
                    "serialNumber",
                    "SN-API-STATE",
                    ProductDisposition.InProcess,
                    ResultJudgement.Unknown,
                    runId,
                    unitLocation,
                    BaseTimeUtc,
                    [operation.OperationRunId])
            ],
            [
                new ProductionLineStationState(
                    "station-api",
                    ProductionLineStationRuntimeStatus.Running,
                    AgentId: "agent-api",
                    StationId: "station-host-api",
                    AgentPresenceSessionId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    AgentPresenceSequence: 2,
                    AgentPresenceState: OpenLineOps.Agent.Contracts.AgentPresenceState.Heartbeat,
                    AgentPresenceHealth: ProductionLineAgentPresenceHealth.Online,
                    AgentPresenceLastSeenAtUtc: BaseTimeUtc.AddSeconds(1),
                    AgentPresenceAge: TimeSpan.Zero,
                    Queue: [new ProductionLineQueuedMaterial(
                        MaterialKind.Carrier,
                        "carrier-api",
                        BaseTimeUtc)],
                    ActiveOperations:
                    [
                        new ProductionLineStationOperationState(
                            runId,
                            productionUnitId,
                            run.ProductionUnitIdentity,
                            operation.OperationRunId,
                            operation.OperationId,
                            ExecutionStatus.Running,
                            ResultJudgement.Unknown,
                            BaseTimeUtc,
                            operationResources)
                    ])
            ],
            [
                new ProductionLineSlotState(
                    "station-api",
                    "slot-api",
                    SlotOccupancyStatus.Running,
                    material,
                    BaseTimeUtc)
            ],
            [
                new ProductionLineCarrierState(
                    "carrier-api",
                    "tray-api",
                    12,
                    carrierLocation,
                    BaseTimeUtc,
                    [
                        new ProductionLineCarrierPositionState(
                            "position-01",
                            carriedUnitId,
                            ProductDisposition.InProcess,
                            ResultJudgement.Unknown)
                    ])
            ]);
    }

    private sealed class StubLineRuntimeStateReader(ProductionLineRuntimeState state) :
        IProductionLineRuntimeStateReader
    {
        public ValueTask<ProductionLineRuntimeState> ReadAsync(
            string productionLineDefinitionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(state.ProductionLineDefinitionId, productionLineDefinitionId);
            return ValueTask.FromResult(state);
        }
    }
}
