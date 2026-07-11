using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryRepositoryAtomicallyStoresRunAndFrozenExecutionPlan()
    {
        var repository = new InMemoryProductionRunRepository();
        var (run, plan) = CreateRun();

        Assert.True(await repository.TryAddAsync(run, plan));
        var restored = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id));
        var restoredPlan = Assert.IsType<ProductionRunExecutionPlan>(
            await repository.GetByRunIdAsync(run.Id));

        Assert.Equal(0, restored.Revision);
        Assert.Equal("product.board", restored.Run.ProductionUnitIdentity.ModelId);
        Assert.Equal("operation.main", Assert.Single(restoredPlan.Operations).Definition.OperationId);
    }

    [Fact]
    public async Task SqliteRepositoryRoundTripsDualAxesAndTypedOutput()
    {
        await using var database = new TemporaryDatabase();
        using var repository = new SqliteProductionRunRepository(database.ConnectionString);
        var (run, plan) = CreateRun();
        Assert.True(await repository.TryAddAsync(run, plan));
        Assert.True(run.Start(Now).Succeeded);
        var operation = Assert.Single(run.Operations);
        var leases = operation.ResourceRequirements.Select(resource => new ResourceLease(
            resource,
            run.Id,
            operation.OperationRunId,
            1,
            Now,
            Now.AddHours(1))).ToArray();
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            leases,
            Now).Succeeded);
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Failed,
            new Dictionary<string, ProductionContextValue>
            {
                ["measuredVoltage"] = new(
                    ProductionContextValueKind.FixedPoint,
                    "3.30")
            },
            1,
            1,
            0,
            Now.AddSeconds(1)).Succeeded);

        Assert.Equal(1, await repository.SaveAsync(run, 0));
        var restored = Assert.IsType<ProductionRunPersistenceEntry>(
            await repository.GetByIdAsync(run.Id));

        Assert.Equal(ExecutionStatus.Completed, restored.Run.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, restored.Run.Judgement);
        Assert.Equal(ProductDisposition.Nonconforming, restored.Run.Disposition);
        Assert.Equal(
            "3.30",
            Assert.Single(restored.Run.Operations).Outputs["measuredVoltage"].CanonicalValue);
        Assert.NotNull(await repository.GetByRunIdAsync(run.Id));
    }

    [Fact]
    public async Task RepositoryRejectsStaleAggregateRevision()
    {
        var repository = new InMemoryProductionRunRepository();
        var (run, plan) = CreateRun();
        Assert.True(await repository.TryAddAsync(run, plan));
        Assert.True(run.Start(Now).Succeeded);
        Assert.Equal(1, await repository.SaveAsync(run, 0));

        await Assert.ThrowsAsync<ProductionRunConcurrencyException>(async () =>
            await repository.SaveAsync(run, 0));
    }

    [Fact]
    public async Task ResourceLeasesAreExclusiveAndFencingTokensIncreaseAfterRelease()
    {
        var leases = new InMemoryResourceLeaseRepository();
        var resource = new ResourceRequirement(ResourceKind.Station, "station.main");
        var firstRun = ProductionRunId.New();
        var secondRun = ProductionRunId.New();

        var first = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await leases.TryAcquireAsync(
                firstRun,
                "operation@0001",
                [resource],
                Now,
                TimeSpan.FromMinutes(1))));
        Assert.Null(await leases.TryAcquireAsync(
            secondRun,
            "operation@0001",
            [resource],
            Now.AddSeconds(1),
            TimeSpan.FromMinutes(1)));

        await leases.ReleaseAsync(firstRun, "operation@0001");
        var second = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await leases.TryAcquireAsync(
                secondRun,
                "operation@0001",
                [resource],
                Now.AddSeconds(2),
                TimeSpan.FromMinutes(1))));
        Assert.True(second.FencingToken > first.FencingToken);
    }

    private static (ProductionRun Run, ProductionRunExecutionPlan Plan) CreateRun()
    {
        var runId = ProductionRunId.New();
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.main"),
            new ProcessVersionId("process-version.main"),
            []);
        var operation = new OperationExecutionPlan(
            "operation.main",
            "station.main",
            new StationId("station.main"),
            new ConfigurationSnapshotId("configuration.main"),
            new RecipeSnapshotId("recipe.main"),
            process);
        var run = ProductionRun.Create(
            runId,
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-001"),
            null,
            null,
            "operator.main",
            operation.Definition.OperationId,
            Now,
            [operation.Definition],
            []);
        return (run, new ProductionRunExecutionPlan(runId, [operation]));
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-runtime-{Guid.NewGuid():N}.sqlite");

        public string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        public ValueTask DisposeAsync()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return ValueTask.CompletedTask;
        }
    }
}
