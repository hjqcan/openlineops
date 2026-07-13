using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Api.HostedServices;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionRunCoordinatorHostedServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DiscoversLaterRunWhileEarlierRunIsStillExecuting()
    {
        var first = CreateRun("first");
        var second = CreateRun("second");
        var repository = new MutableActiveRunRepository();
        repository.Add(first);
        var runner = new BlockingRunner(first.Id, second.Id);
        var services = new ServiceCollection();
        services.AddSingleton<IProductionRunRepository>(repository);
        services.AddSingleton<IProductionRunRunner>(runner);
        await using var provider = services.BuildServiceProvider();
        var service = new ProductionRunCoordinatorHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProductionRunCoordinatorHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await runner.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            repository.Add(second);

            await runner.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, runner.ExecutionCount(first.Id));
            Assert.Equal(1, runner.ExecutionCount(second.Id));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private static ProductionRun CreateRun(string suffix)
    {
        var operation = new OperationRunDefinition(
            $"operation.{suffix}",
            $"station.{suffix}",
            new StationId($"station.{suffix}"),
            new ProcessDefinitionId($"process.{suffix}"),
            new ProcessVersionId($"process-version.{suffix}"),
            new ConfigurationSnapshotId($"configuration.{suffix}"),
            new RecipeSnapshotId($"recipe.{suffix}"));
        return ProductionRun.Create(
            ProductionRunId.New(),
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", $"SN-{suffix}"),
            null,
            null,
            "operator.main",
            operation.OperationId,
            Now,
            [operation],
            []);
    }

    private sealed class BlockingRunner(
        ProductionRunId firstRunId,
        ProductionRunId secondRunId) : IProductionRunRunner
    {
        private readonly object _gate = new();
        private readonly Dictionary<ProductionRunId, int> _counts = [];

        public TaskCompletionSource<bool> FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<Result<ProductionRunRunResult>> ExecuteAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _counts[runId] = _counts.GetValueOrDefault(runId) + 1;
            }

            if (runId == firstRunId)
            {
                FirstStarted.TrySetResult(true);
            }
            else if (runId == secondRunId)
            {
                SecondStarted.TrySetResult(true);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The test runner must be canceled by host shutdown.");
        }

        public int ExecutionCount(ProductionRunId runId)
        {
            lock (_gate)
            {
                return _counts.GetValueOrDefault(runId);
            }
        }
    }

    private sealed class MutableActiveRunRepository : IProductionRunRepository
    {
        private readonly object _gate = new();
        private readonly List<ProductionRunPersistenceEntry> _runs = [];

        public void Add(ProductionRun run)
        {
            lock (_gate)
            {
                _runs.Add(new ProductionRunPersistenceEntry(run, 0));
            }
        }

        public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListActiveAsync(
            string? productionLineDefinitionId = null,
            string? stationSystemId = null,
            string? slotId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult<IReadOnlyCollection<ProductionRunPersistenceEntry>>(
                    _runs.ToArray());
            }
        }

        public ValueTask<bool> TryAddAsync(
            ProductionRun run,
            ProductionRunExecutionPlan executionPlan,
            ProductionRunAdmission admission,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<long> SaveAsync(
            ProductionRun run,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ProductionRunPersistenceEntry?> GetByIdAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListRecoverableAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyCollection<ProductionRunCreatedOutboxItem>>
            ListPendingCreatedOutboxAsync(
                int maximumCount,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask MarkCreatedOutboxProcessedAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask RecordCreatedOutboxFailureAsync(
            ProductionRunId runId,
            string failureDescription,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyCollection<ProductionRunTerminalOutboxItem>>
            ListPendingTerminalOutboxAsync(
                int maximumCount,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask MarkTerminalOutboxProcessedAsync(
            ProductionRunId runId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask RecordTerminalOutboxFailureAsync(
            ProductionRunId runId,
            string failureDescription,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
