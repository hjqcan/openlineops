using OpenLineOps.Runtime.Application.Events;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionRunTerminalOutboxDispatcherTests
{
    [Fact]
    public async Task TerminalSnapshotIsDispatchedExactlyOnce()
    {
        var materials = new InMemoryProductionMaterialRepository();
        var repository = new InMemoryProductionRunRepository(materials);
        var now = new DateTimeOffset(2026, 7, 11, 5, 0, 0, TimeSpan.Zero);
        var operation = new OperationExecutionPlan(
            "operation.main",
            "station.main",
            new StationId("station.main"),
            new ConfigurationSnapshotId("configuration.main"),
            new RecipeSnapshotId("recipe.main"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId("process.main"),
                new ProcessVersionId("process-version.main"),
                []));
        var run = ProductionRun.Create(
            ProductionRunId.New(),
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-001"),
            null,
            null,
            "operator.main",
            operation.Definition.OperationId,
            now,
            [operation.Definition],
            []);
        Assert.True(await repository.TryAddAsync(
            run,
            new ProductionRunExecutionPlan(run.Id, [operation]),
            await ProductionRunTestMaterials.RegisterAsync(materials, run)));
        Assert.True(run.Start(now).Succeeded);
        var operationRun = Assert.Single(run.Operations);
        var leases = operationRun.ResourceRequirements.Select(resource => new ResourceLease(
            resource,
            run.Id,
            operationRun.OperationRunId,
            1,
            now,
            now.AddHours(1))).ToArray();
        Assert.True(run.StartOperation(
            operationRun.OperationRunId,
            RuntimeSessionId.New(),
            leases,
            now).Succeeded);
        Assert.True(run.CompleteOperation(
            operationRun.OperationRunId,
            ResultJudgement.Passed,
            null,
            1,
            1,
            0,
            now.AddSeconds(1)).Succeeded);
        await repository.SaveAsync(run, 0);
        var handler = new RecordingHandler();
        using var dispatcher = new ProductionRunTerminalOutboxDispatcher(repository, [handler]);

        Assert.Equal(1, await dispatcher.DrainAsync());
        Assert.Equal(0, await dispatcher.DrainAsync());
        Assert.Equal(run.Id, Assert.Single(handler.Runs).RunId);
    }

    private sealed class RecordingHandler : IProductionRunTerminalOutboxHandler
    {
        public List<ProductionRunSnapshot> Runs { get; } = [];

        public ValueTask HandleAsync(
            ProductionRunSnapshot run,
            CancellationToken cancellationToken = default)
        {
            Runs.Add(run);
            return ValueTask.CompletedTask;
        }
    }
}
