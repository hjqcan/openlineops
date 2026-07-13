using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresSchemaInitializationIntegrationTests(
    PostgresContainerFixture fixture)
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);

    [PostgresIntegrationFact]
    public async Task IndependentRepositoriesSerializeRepeatedConcurrentFirstSchemaInitialization()
    {
        const int rounds = 4;
        const int workersPerRepository = 4;

        for (var round = 0; round < rounds; round++)
        {
            await using var schema = await PostgresIsolatedSchema.CreateAsync(
                fixture.ConnectionString,
                "schemarace");
            var initializers = Enumerable.Range(0, workersPerRepository)
                .SelectMany(worker => new Task[]
                {
                    InitializeMaterialRepositoryAsync(schema.ConnectionString, round, worker),
                    InitializeCoordinationStoreAsync(schema.ConnectionString)
                })
                .ToArray();

            await Task.WhenAll(initializers).WaitAsync(TimeSpan.FromSeconds(30));

            await using var materialVerification =
                new PostgreSqlProductionMaterialRepository(schema.ConnectionString);
            Assert.Equal(
                workersPerRepository,
                (await materialVerification.ListProductionUnitsAsync()).Count);
            using var coordinationVerification =
                new PostgreSqlProductionCoordinationStore(schema.ConnectionString);
            Assert.Empty(await coordinationVerification.ListAsync());
        }
    }

    private static async Task InitializeMaterialRepositoryAsync(
        string connectionString,
        int round,
        int worker)
    {
        await using var repository =
            new PostgreSqlProductionMaterialRepository(connectionString);
        var unit = ProductionUnit.Register(
            ProductionUnitId.New(),
            "schema-race-board",
            "serial-number",
            $"SCHEMA-{round:D2}-{worker:D2}",
            null,
            "integration-test",
            BaseTimeUtc);

        Assert.True(await repository.TryAddAsync(unit));
    }

    private static async Task InitializeCoordinationStoreAsync(string connectionString)
    {
        using var store = new PostgreSqlProductionCoordinationStore(connectionString);
        Assert.Empty(await store.ListAsync());
    }
}
