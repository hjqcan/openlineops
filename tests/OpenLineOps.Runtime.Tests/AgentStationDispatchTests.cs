using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
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
        Assert.Equal("station.main", message.StationSystemId);
        Assert.Equal(request.ResourceLeases.Count, message.ResourceFences.Count);
        Assert.All(message.ResourceFences, fence =>
        {
            Assert.True(fence.FencingToken > 0);
            Assert.True(fence.ExpiresAtUtc > Now);
        });
        Assert.Equal(ExecutionStatus.Completed, result.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, result.Judgement);
        Assert.Equal("true", result.Outputs["accepted"].CanonicalValue);
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
            "Guard opened."));

        Assert.True(result.Accepted);
        var message = Assert.IsType<StationSafeStopRequested>(gateway.Request);
        Assert.Equal(request.Run.RunId.Value, message.ProductionRunId);
        Assert.Equal("operation.main@0001", message.OperationRunId);
        Assert.Equal("operator.safety", message.ActorId);
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
            [
                new ResourceRequirement(ResourceKind.Station, "station.main"),
                new ResourceRequirement(ResourceKind.Slot, "slot.01"),
                new ResourceRequirement(ResourceKind.Device, "device.tester")
            ]);
        var run = ProductionRun.Create(
            runId,
            "project.main",
            "application.main",
            "snapshot.main",
            "topology.main",
            "line.main",
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
            leases);
    }

    private sealed class FixedDeploymentResolver : IStationDeploymentResolver
    {
        public ValueTask<StationDeploymentRoute> ResolveAsync(
            StationDeploymentRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StationDeploymentRoute(
                "agent.main",
                "station.main",
                new string('a', 64)));
    }

    private sealed class RecordingJobGateway : IStationJobGateway
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
                request.AgentId,
                request.StationId,
                ExecutionStatus.Completed,
                ResultJudgement.Passed,
                outputs.RootElement.Clone(),
                [],
                null,
                null,
                Now.AddSeconds(1)));
        }
    }

    private sealed class RecordingSafetyGateway : IStationSafetyGateway
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
                request.AgentId,
                request.StationId,
                true,
                null,
                null,
                Now.AddSeconds(1)));
        }
    }
}
