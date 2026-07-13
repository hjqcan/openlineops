using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresProductionCoordinationColdStartIntegrationTests(
    PostgresContainerFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 13, 0, 0, TimeSpan.Zero);

    [PostgresIntegrationFact]
    public async Task RunPlanLeaseDispatchOutboxAndResultInboxSurviveColdRestartExactlyOnce()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var unitId = ProductionUnitId.New();
        var operationPlan = OperationPlan(suffix);
        var run = ProductionRun.Create(
            ProductionRunId.New(),
            $"project-{suffix}",
            $"application-{suffix}",
            $"snapshot-{suffix}",
            $"topology-{suffix}",
            $"line-{suffix}",
            unitId,
            new ProductionUnitIdentity(
                $"product-{suffix}",
                "serialNumber",
                $"SN-{suffix}"),
            null,
            null,
            $"operator-{suffix}",
            operationPlan.Definition.OperationId,
            Now,
            [operationPlan.Definition],
            []);
        var plan = new ProductionRunExecutionPlan(run.Id, [operationPlan]);
        StationJobRequested request;
        ResourceLeaseChanged change;
        StationJobAccepted accepted;
        StationJobProgressed progress;
        StationJobCompleted completion;
        ResourceLease[] acquired;
        using (var materials = new PostgreSqlProductionMaterialRepository(fixture.ConnectionString))
        using (var store = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString))
        {
            Assert.True(await materials.TryAddAsync(ProductionUnit.Register(
                unitId,
                run.ProductionUnitIdentity.ModelId,
                run.ProductionUnitIdentity.InputKey,
                run.ProductionUnitIdentity.Value,
                null,
                run.ActorId,
                Now.AddMinutes(-1))));
            var unit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
                await materials.GetProductionUnitAsync(unitId));
            Assert.True(await store.TryAddAsync(
                run,
                plan,
                new ProductionRunAdmission(unit.Aggregate.ToSnapshot(), unit.Revision)));
            Assert.True(run.Start(Now.AddSeconds(1)).Succeeded);
            var operation = Assert.Single(run.Operations);
            acquired = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
                    await store.TryAcquireAsync(
                        run.Id,
                        operation.OperationRunId,
                        operation.ResourceRequirements,
                        TimeSpan.FromMinutes(5)))
                .ToArray();
            Assert.True(run.StartOperation(
                operation.OperationRunId,
                RuntimeSessionId.New(),
                acquired,
                Now.AddSeconds(2)).Succeeded);
            Assert.Equal(1, await store.SaveAsync(run, 0));

            request = JobRequest(
                run,
                Assert.Single(run.ToSnapshot().Operations),
                acquired,
                suffix);
            change = StationDispatchMessageIdentity.CreateLeaseGranted(
                request,
                Assert.Single(request.ResourceFences));
            Assert.True(await store.TryEnqueueAsync(request, [change]));
            accepted = new StationJobAccepted(
                Guid.NewGuid(),
                request.JobId,
                request.IdempotencyKey,
                request.AgentId,
                request.StationId,
                Now.AddSeconds(3));
            progress = new StationJobProgressed(
                Guid.NewGuid(),
                request.JobId,
                request.IdempotencyKey,
                request.AgentId,
                request.StationId,
                50,
                "Executing",
                Now.AddSeconds(4));
            completion = Completion(request, ResultJudgement.Passed);
            await store.RecordAcceptedAsync(accepted);
            await store.RecordProgressAsync(progress);
            await store.RecordCompletionAsync(completion);
        }

        using (var restarted = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString))
        {
            var restored = Assert.IsType<ProductionRunPersistenceEntry>(
                await restarted.GetByIdAsync(run.Id));
            var restoredPlan = Assert.IsType<ProductionRunExecutionPlan>(
                await restarted.GetByRunIdAsync(run.Id));
            Assert.Equal(1, restored.Revision);
            Assert.Equal(
                operationPlan.Definition.OperationId,
                Assert.Single(restoredPlan.Operations).Definition.OperationId);

            Assert.False(await restarted.TryEnqueueAsync(request, [change]));
            var pendingLease = Assert.Single(await restarted.ListPendingAsync(10));
            Assert.Equal(nameof(ResourceLeaseChanged), pendingLease.Kind);
            Assert.Null(await restarted.TryAcquireAsync(
                run.Id,
                request.OperationRunId,
                acquired.Select(static lease => lease.Resource).ToArray(),
                TimeSpan.FromHours(1)));

            var events = await restarted.ListEventsAsync(request.JobId);
            Assert.Equal(2, events.Count);
            Assert.Contains(events, item => item.MessageId == accepted.MessageId);
            Assert.Contains(events, item => item.MessageId == progress.MessageId);
            await restarted.RecordAcceptedAsync(accepted);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await restarted.RecordAcceptedAsync(accepted with
                {
                    MessageId = Guid.NewGuid()
                }));
            var restoredCompletion = Assert.IsType<StationJobCompleted>(
                await restarted.GetCompletionAsync(request.IdempotencyKey));
            Assert.Equal(completion.MessageId, restoredCompletion.MessageId);
            await restarted.RecordCompletionAsync(completion);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await restarted.RecordCompletionAsync(
                    Completion(request, ResultJudgement.Failed) with
                    {
                        MessageId = completion.MessageId
                    }));
            await restarted.MarkPublishedAsync(pendingLease.MessageId);
            var pendingJob = Assert.Single(await restarted.ListPendingAsync(10));
            Assert.Equal(nameof(StationJobRequested), pendingJob.Kind);
            await restarted.MarkPublishedAsync(pendingJob.MessageId);

            await restarted.ReleaseAsync(
                run.Id,
                request.OperationRunId,
                acquired.Select(ResourceLeaseReleaseClaim.FromLease).ToArray());
        }

        using var secondRestart = new PostgreSqlProductionCoordinationStore(
            fixture.ConnectionString);
        Assert.Empty(await secondRestart.ListPendingAsync(10));
        Assert.NotNull(await secondRestart.GetCompletionAsync(request.IdempotencyKey));
    }

    private static OperationExecutionPlan OperationPlan(string suffix)
    {
        var operationId = $"operation-{suffix}";
        var stationSystemId = $"station-system-{suffix}";
        var process = new ExecutableRuntimeProcess(
            new ProcessDefinitionId($"process-{suffix}"),
            new ProcessVersionId($"process-version-{suffix}"),
            []);
        return new OperationExecutionPlan(
            operationId,
            stationSystemId,
            new StationId($"station-{suffix}"),
            new ConfigurationSnapshotId($"configuration-{suffix}"),
            new RecipeSnapshotId($"recipe-{suffix}"),
            process);
    }

    private static StationJobRequested JobRequest(
        ProductionRun run,
        OperationRunSnapshot operation,
        IReadOnlyCollection<ResourceLease> leases,
        string suffix)
    {
        using var inputs = JsonDocument.Parse("{}");
        var idempotencyKey = $"job/{run.Id.Value:D}/{operation.OperationRunId}";
        return new StationJobRequested(
            Guid.NewGuid(),
            StationJobIdentity.CreateJobId(idempotencyKey),
            idempotencyKey,
            $"agent-{suffix}",
            $"station-{suffix}",
            operation.Definition.StationSystemId,
            run.Id.Value,
            run.ProductionUnitId.Value,
            operation.RuntimeSessionId!.Value.Value,
            operation.OperationRunId,
            operation.Attempt,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            run.LotId,
            run.CarrierId,
            run.ProjectId,
            run.ApplicationId,
            run.ProjectSnapshotId,
            run.ProductionLineDefinitionId,
            run.TopologyId,
            run.ActorId,
            new string('a', 64),
            operation.Definition.OperationId,
            operation.Definition.ProcessDefinitionId.Value,
            operation.Definition.ProcessVersionId.Value,
            operation.Definition.ConfigurationSnapshotId.Value,
            operation.Definition.RecipeSnapshotId.Value,
            leases.Select(static lease => new StationResourceFence(
                lease.Resource.Kind.ToString(),
                lease.Resource.ResourceId,
                lease.FencingToken,
                lease.ExpiresAtUtc)).ToArray(),
            inputs.RootElement.Clone(),
            Now.AddSeconds(2));
    }

    private static StationJobCompleted Completion(
        StationJobRequested request,
        ResultJudgement judgement)
    {
        using var outputs = JsonDocument.Parse("{}");
        return new StationJobCompleted(
            Guid.NewGuid(),
            request.JobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            request.RuntimeSessionId,
            ExecutionStatus.Completed,
            judgement,
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
            Now.AddSeconds(5));
    }
}
