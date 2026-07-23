using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Application.Identifiers;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Sessions;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Targets;
using OpenLineOps.Runtime.Infrastructure.Events;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ExecutableRuntimeProcessExecutionBoundsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CountedLoopLeaseCoversEveryTraversalBeforeAnotherRunCanAcquireResource()
    {
        var process = CountedLoopProcess(3);
        var bounds = ExecutableRuntimeProcessExecutionBounds.Calculate(process);
        var fullWorstPath = TimeSpan.FromMinutes(42);
        Assert.True(bounds.MaximumNodeExecutionTime >= fullWorstPath);

        var clock = new MutableClock(Now);
        var repository = new InMemoryResourceLeaseRepository(clock);
        var resource = new ResourceRequirement(ResourceKind.Device, "device.looping-vendor");
        var first = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                ProductionRunId.New(),
                "operation.loop@0001",
                [resource],
                bounds.MaximumNodeExecutionTime + TimeSpan.FromMinutes(5))));

        clock.UtcNow = Now + fullWorstPath;
        Assert.True(clock.UtcNow < first.ExpiresAtUtc);
        Assert.Null(await repository.TryAcquireAsync(
            ProductionRunId.New(),
            "operation.competing@0001",
            [resource],
            TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task UncountedCycleIsRejectedBeforeSessionOrCommandExecution()
    {
        var node = Node("node.unbounded", TimeSpan.FromSeconds(1));
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.unbounded"),
            new ProcessVersionId("process.unbounded@1"),
            [node])
        {
            StartNodeId = node.NodeId,
            Transitions = [new ExecutableRuntimeTransition(node.NodeId, node.NodeId, null)]
        };
        var repository = new InMemoryRuntimeSessionRepository();
        var executor = new CountingCommandExecutor();
        var runner = new RuntimeSessionRunner(
            repository,
            new InMemoryRuntimeDomainEventPublisher(),
            executor,
            new GuidRuntimeIdProvider(),
            new MutableClock(Now));
        var sessionId = RuntimeSessionId.New();

        var result = await runner.RunAsync(new StartRuntimeSessionRequest(
            sessionId,
            new StationId("station.bounds"),
            new ConfigurationSnapshotId("configuration.bounds"),
            new RecipeSnapshotId("recipe.bounds"),
            process,
            new Dictionary<string, ProductionContextValue>(),
            RuntimeTestReleaseIdentity.TraceMetadata()));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Runtime.ProcessExecutionBoundsInvalid", result.Error.Code);
        Assert.Contains("without an explicit counted transition", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, executor.InvocationCount);
        Assert.Null(await repository.GetByIdAsync(sessionId));
    }

    [Fact]
    public void DurationAndTraversalCounterOverflowAreRejected()
    {
        var durationOverflow = new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.duration-overflow"),
            new ProcessVersionId("process.duration-overflow@1"),
            [
                Node("node.duration-overflow-a", TimeSpan.MaxValue),
                Node("node.duration-overflow-b", TimeSpan.MaxValue)
            ]);
        Assert.Throws<InvalidDataException>(() =>
            ExecutableRuntimeProcessExecutionBounds.Calculate(durationOverflow));

        var traversalOverflow = CountedLoopProcess(int.MaxValue);
        var exception = Assert.Throws<InvalidDataException>(() =>
            ExecutableRuntimeProcessExecutionBounds.Calculate(traversalOverflow));
        Assert.Contains("traversal capacity", exception.Message, StringComparison.Ordinal);
    }

    private static ExecutableRuntimeProcess CountedLoopProcess(int maxTraversals)
    {
        var work = Node("node.loop-work", TimeSpan.FromMinutes(10));
        var finish = Node("node.loop-finish", TimeSpan.FromMinutes(2));
        var route = new ExecutableRuntimeRoutingNode(
            new RuntimeNodeId("node.loop-route"),
            "Route loop",
            ExecutableRuntimeRoutingNodeKind.Decision);
        return new ExecutableRuntimeProcess(
            new ProcessDefinitionId("process.counted-loop"),
            new ProcessVersionId("process.counted-loop@1"),
            [work, finish])
        {
            StartNodeId = work.NodeId,
            RoutingNodes = [route],
            Transitions =
            [
                new ExecutableRuntimeTransition(work.NodeId, route.NodeId, null),
                new ExecutableRuntimeTransition(route.NodeId, work.NodeId, "retry", maxTraversals),
                new ExecutableRuntimeTransition(route.NodeId, finish.NodeId, "done")
            ]
        };
    }

    private static ExecutableRuntimeNode Node(string id, TimeSpan timeout)
    {
        var nodeId = new RuntimeNodeId(id);
        var capability = new RuntimeCapabilityId("capability.bounds");
        return new ExecutableRuntimeNode(
            nodeId,
            id,
            capability,
            "Execute",
            timeout,
            null,
            new RuntimeActionId($"action.{id}"),
            new RuntimeTargetReference(RuntimeTargetKinds.Capability, capability.Value));
    }

    private sealed class CountingCommandExecutor : IRuntimeCommandExecutor
    {
        public int InvocationCount { get; private set; }

        public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeCommandExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return ValueTask.FromResult(RuntimeCommandExecutionResult.Completed());
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
