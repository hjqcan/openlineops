using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionOperationsApiTests :
    IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private const string Endpoint = "/api/operations/active-runs";
    private readonly OpenLineOpsApiWebApplicationFactory _factory;

    public ProductionOperationsApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ActiveRunQueryForwardsExactLineStationAndCanonicalSlotScope()
    {
        var repository = new RecordingProductionRunRepository();
        using var factory = Factory(repository);
        using var client = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);
        var slotResourceId = Uri.EscapeDataString("line.api/station.api/slot.api");

        using var response = await client.GetAsync(
            $"{Endpoint}?productionLineDefinitionId=line.api"
            + $"&stationSystemId=station.api&slotResourceId={slotResourceId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            repository.ActiveQueries,
            query => query.ProductionLineDefinitionId == "line.api"
                && query.StationSystemId == "station.api"
                && query.SlotResourceId == "line.api/station.api/slot.api");
    }

    [Theory]
    [InlineData("?slotId=slot.api")]
    [InlineData("?slotResourceId=slot.api")]
    [InlineData("?productionLineDefinitionId=line.api&slotResourceId=other.line/station.api/slot.api")]
    [InlineData("?stationSystemId=station.api&slotResourceId=line.api/other.station/slot.api")]
    [InlineData("?stationSystemId=station.api&stationSystemId=station.other")]
    public async Task ActiveRunQueryRejectsLegacyMalformedInconsistentOrRepeatedFields(
        string query)
    {
        var repository = new RecordingProductionRunRepository();
        using var factory = Factory(repository);
        using var client = factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);

        using var response = await client.GetAsync($"{Endpoint}{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private WebApplicationFactory<Program> Factory(
        IProductionRunRepository repository) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProductionRunRepository>();
                services.AddSingleton(repository);
            });
        });

    private sealed class RecordingProductionRunRepository : IProductionRunRepository
    {
        public ConcurrentQueue<ProductionRunActiveQuery> ActiveQueries { get; } = new();

        public ValueTask<bool> TryAddAsync(
            ProductionRun run,
            ProductionRunExecutionPlan executionPlan,
            ProductionRunAdmission admission,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask<long> SaveAsync(
            ProductionRun run,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(expectedRevision);

        public ValueTask<ProductionRunPersistenceEntry?> GetByIdAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProductionRunPersistenceEntry?>(null);

        public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListRecoverableAsync(
            CancellationToken cancellationToken = default) =>
            EmptyEntries();

        public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListActiveAsync(
            ProductionRunActiveQuery query,
            CancellationToken cancellationToken = default)
        {
            ActiveQueries.Enqueue(query);
            return EmptyEntries();
        }

        public ValueTask<ProductionRunTerminalPage> ListTerminalAsync(
            ProductionRunTerminalPageRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProductionRunTerminalPage([], null));

        public ValueTask<IReadOnlyCollection<ProductionRunCreatedOutboxItem>>
            ListPendingCreatedOutboxAsync(
                int maximumCount,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyCollection<ProductionRunCreatedOutboxItem>>([]);

        public ValueTask MarkCreatedOutboxProcessedAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask RecordCreatedOutboxFailureAsync(
            ProductionRunId runId,
            string failureDescription,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IReadOnlyCollection<ProductionRunTerminalOutboxItem>>
            ListPendingTerminalOutboxAsync(
                int maximumCount,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyCollection<ProductionRunTerminalOutboxItem>>([]);

        public ValueTask MarkTerminalOutboxProcessedAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask RecordTerminalOutboxFailureAsync(
            ProductionRunId runId,
            string failureDescription,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        private static ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>>
            EmptyEntries() =>
            ValueTask.FromResult<IReadOnlyCollection<ProductionRunPersistenceEntry>>([]);
    }
}
