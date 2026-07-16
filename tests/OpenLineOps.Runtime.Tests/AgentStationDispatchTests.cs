using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Commands;

namespace OpenLineOps.Runtime.Tests;

public sealed class AgentStationDispatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AgentDispatcherMapsImmutableIdentityAndEveryResourceFence()
    {
        var request = CreateDispatchRequest();
        var gateway = new RecordingJobGateway();
        var dispatcher = new AgentStationOperationDispatcher(
            gateway,
            new FixedDeploymentResolver());

        var result = await dispatcher.DispatchAsync(request);
        var message = Assert.IsType<StationJobRequested>(gateway.Request);

        Assert.Equal(request.IdempotencyKey, message.IdempotencyKey);
        Assert.Equal("operation.main@0001", message.OperationRunId);
        Assert.Equal(1, message.OperationAttempt);
        Assert.Equal("product.board", message.ProductModelId);
        Assert.Equal("SN-001", message.ProductionUnitIdentityValue);
        Assert.Equal(request.Run.ProductionUnitId.Value, message.ProductionUnitId);
        Assert.Equal(request.RuntimeSessionId.Value, message.RuntimeSessionId);
        Assert.Equal(request.Run.ProductionLineDefinitionId, message.ProductionLineDefinitionId);
        Assert.Equal(request.Run.TopologyId, message.TopologyId);
        Assert.Equal(request.Run.ActorId, message.ActorId);
        Assert.Equal("station.main", message.StationSystemId);
        Assert.Equal("physical.main", message.StationId);
        var productionInputs = ProductionContextDocument.Read(message.Inputs);
        Assert.Equal(2, productionInputs.Count);
        Assert.Equal(
            new ProductionContextValue(ProductionContextValueKind.Text, "recipe-a"),
            productionInputs["recipe.name"]);
        Assert.Equal(
            new ProductionContextValue(ProductionContextValueKind.WholeNumber, "3"),
            productionInputs["fixture.count"]);
        Assert.Equal(request.ResourceLeases.Count, message.ResourceFences.Count);
        Assert.All(message.ResourceFences, fence =>
        {
            Assert.True(fence.FencingToken > 0);
            Assert.True(fence.ExpiresAtUtc > Now);
        });
        Assert.Equal(ExecutionStatus.Completed, result.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, result.Judgement);
        Assert.Equal("true", result.Outputs["accepted"].CanonicalValue);
        Assert.Equal(0, result.CompletedStepCount);
        Assert.Equal(0, result.CommandCount);
        Assert.Equal(0, result.IncidentCount);
        Assert.Equal("station.main", result.ExecutionEvidence.StationId);
    }

    [Fact]
    public async Task SafetyControllerWaitsForMatchingAgentAcknowledgement()
    {
        var request = CreateDispatchRequest();
        var gateway = new RecordingSafetyGateway();
        var controller = new AgentStationSafetyController(
            gateway,
            new FixedDeploymentResolver());

        var result = await controller.RequestSafeStopAsync(new StationSafetyRequest(
            request.Run,
            "operator.safety",
            "Guard opened.",
            Now));

        Assert.True(result.Accepted);
        var message = Assert.IsType<StationSafeStopRequested>(gateway.Request);
        Assert.Equal(request.Run.RunId.Value, message.ProductionRunId);
        Assert.Equal("operation.main@0001", message.OperationRunId);
        Assert.Equal("operator.safety", message.ActorId);
    }

    [Fact]
    public async Task SafetyControllerDoesNotDispatchPhysicalStopForPendingStationWork()
    {
        var request = CreateDispatchRequest();
        var pendingRun = request.Run with
        {
            ExecutionStatus = ExecutionStatus.Pending,
            Operations = request.Run.Operations.Select(operation => operation with
            {
                ExecutionStatus = ExecutionStatus.Pending,
                RuntimeSessionId = null,
                StartedAtUtc = null,
                FencingTokens = new Dictionary<ResourceRequirement, long>()
            }).ToArray()
        };
        var gateway = new RecordingSafetyGateway();
        var controller = new AgentStationSafetyController(
            gateway,
            new FixedDeploymentResolver());

        var result = await controller.RequestSafeStopAsync(new StationSafetyRequest(
            pendingRun,
            "operator.safety",
            "Cancel before dispatch.",
            Now));

        Assert.True(result.Accepted);
        Assert.Null(gateway.Request);
    }

    [Fact]
    public async Task SafetyControllerRetryUsesIdenticalDurableMessageEvidence()
    {
        var request = CreateDispatchRequest();
        var gateway = new RecordingSafetyGateway();
        var controller = new AgentStationSafetyController(
            gateway,
            new FixedDeploymentResolver());
        var safetyRequest = new StationSafetyRequest(
            request.Run,
            "operator.safety",
            "Guard opened.",
            Now);

        Assert.True((await controller.RequestSafeStopAsync(safetyRequest)).Accepted);
        var first = Assert.IsType<StationSafeStopRequested>(gateway.Request);
        Assert.True((await controller.RequestSafeStopAsync(safetyRequest)).Accepted);
        var retry = Assert.IsType<StationSafeStopRequested>(gateway.Request);

        Assert.Equal(first, retry);
        Assert.Equal(StationJobIdentity.CreateSafetyMessageId(first.IdempotencyKey), first.MessageId);
        Assert.EndsWith($"/{first.OperationRunId}", first.IdempotencyKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SafetyControllerStillStopsOperationThatCompletedAfterDurableBarrier()
    {
        var request = CreateDispatchRequest();
        var completedAfterBarrier = request.Run with
        {
            ExecutionStatus = ExecutionStatus.Completed,
            Operations = request.Run.Operations.Select(operation => operation with
            {
                ExecutionStatus = ExecutionStatus.Completed,
                Judgement = ResultJudgement.Passed,
                CompletedAtUtc = Now.AddSeconds(1)
            }).ToArray()
        };
        var gateway = new RecordingSafetyGateway();
        var controller = new AgentStationSafetyController(
            gateway,
            new FixedDeploymentResolver());

        var result = await controller.RequestSafeStopAsync(new StationSafetyRequest(
            completedAfterBarrier,
            "operator.safety",
            "Barrier already won the dispatch race.",
            Now));

        Assert.True(result.Accepted);
        Assert.NotNull(gateway.Request);
        Assert.Equal(Assert.Single(completedAfterBarrier.Operations).OperationRunId,
            gateway.Request.OperationRunId);
    }

    [Fact]
    public async Task DispatcherRejectsSpoofedAgentCompletionIdentity()
    {
        var dispatcher = new AgentStationOperationDispatcher(
            new RecordingJobGateway(spoofIdentity: true),
            new FixedDeploymentResolver());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await dispatcher.DispatchAsync(CreateDispatchRequest()));
        Assert.Contains("different idempotent job", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SafetyControllerRejectsSpoofedStationAcknowledgementIdentity()
    {
        var request = CreateDispatchRequest();
        var controller = new AgentStationSafetyController(
            new RecordingSafetyGateway(spoofIdentity: true),
            new FixedDeploymentResolver());

        var result = await controller.RequestSafeStopAsync(new StationSafetyRequest(
            request.Run,
            "operator.safety",
            "Guard opened.",
            Now));

        Assert.False(result.Accepted);
        Assert.Equal("Runtime.SafeStopAcknowledgementMismatch", result.FailureCode);
    }

    [Fact]
    public async Task DispatcherRejectsDeploymentLineBeforeEnqueue()
    {
        var gateway = new RecordingJobGateway();
        var dispatcher = new AgentStationOperationDispatcher(
            gateway,
            new FixedDeploymentResolver("line.spoof"));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await dispatcher.DispatchAsync(CreateDispatchRequest()));
        Assert.Null(gateway.Request);
    }

    private static StationOperationDispatchRequest CreateDispatchRequest()
    {
        var runId = ProductionRunId.New();
        var plan = new OperationExecutionPlan(
            "operation.main",
            "station.main",
            new StationId("station.main"),
            new ConfigurationSnapshotId("configuration.main"),
            new RecipeSnapshotId("recipe.main"),
            new ExecutableRuntimeProcess(
                new ProcessDefinitionId("flow.main"),
                new ProcessVersionId("flow-version.main"),
                []),
            [],
            [
                new ResourceRequirement(ResourceKind.Station, "station.main"),
                new ResourceRequirement(ResourceKind.Slot, "line.main/station.main/slot.01"),
                new ResourceRequirement(ResourceKind.Device, "device.tester")
            ]);
        var run = ProductionRun.Create(
            runId,
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
            ProductionUnitId.New(),
            new ProductionUnitIdentity("product.board", "serialNumber", "SN-001"),
            "lot-001",
            "carrier-001",
            "operator.main",
            "operation.main",
            Now,
            [plan.Definition],
            []);
        Assert.True(run.Start(Now).Succeeded);
        var operation = Assert.Single(run.Operations);
        var leases = operation.ResourceRequirements.Select((resource, index) => new ResourceLease(
            resource,
            run.Id,
            operation.OperationRunId,
            index + 1,
            Now,
            Now.AddMinutes(10))).ToArray();
        var sessionId = RuntimeSessionId.New();
        Assert.True(run.StartOperation(operation.OperationRunId, sessionId, leases, Now).Succeeded);
        var snapshot = run.ToSnapshot();
        return new StationOperationDispatchRequest(
            snapshot,
            Assert.Single(snapshot.Operations),
            plan,
            sessionId,
            new Dictionary<string, ProductionContextValue>
            {
                ["recipe.name"] = new(ProductionContextValueKind.Text, "recipe-a"),
                ["fixture.count"] = new(ProductionContextValueKind.WholeNumber, "3")
            },
            leases);
    }

    private sealed class FixedDeploymentResolver(string lineId = "line.main") :
        IStationDeploymentResolver
    {
        public ValueTask<StationDeploymentRoute> ResolveAsync(
            StationDeploymentRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StationDeploymentRoute(
                "agent.main",
                "physical.main",
                new string('a', 64),
                lineId));
    }

    private sealed class RecordingJobGateway(bool spoofIdentity = false) : IStationJobGateway
    {
        public StationJobRequested? Request { get; private set; }

        public ValueTask<StationJobCompleted> DispatchAsync(
            StationJobRequested request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            using var outputs = JsonDocument.Parse(
                "{\"accepted\":{\"kind\":\"Boolean\",\"value\":\"true\"}}");
            return ValueTask.FromResult(new StationJobCompleted(
                Guid.NewGuid(),
                request.JobId,
                request.IdempotencyKey,
                spoofIdentity ? "agent.spoof" : request.AgentId,
                spoofIdentity ? "station.spoof" : request.StationId,
                request.RuntimeSessionId,
                ExecutionStatus.Completed,
                ResultJudgement.Passed,
                outputs.RootElement.Clone(),
                0,
                0,
                0,
                [],
                [],
                [],
                [],
                null,
                null,
                Now.AddSeconds(1)));
        }
    }

    private sealed class RecordingSafetyGateway(bool spoofIdentity = false) : IStationSafetyGateway
    {
        public StationSafeStopRequested? Request { get; private set; }

        public ValueTask<StationSafeStopAcknowledged> RequestSafeStopAsync(
            StationSafeStopRequested request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(new StationSafeStopAcknowledged(
                Guid.NewGuid(),
                request.MessageId,
                request.IdempotencyKey,
                spoofIdentity ? "agent.spoof" : request.AgentId,
                spoofIdentity ? "station.spoof" : request.StationId,
                true,
                null,
                null,
                Now.AddSeconds(1)));
        }
    }
}
