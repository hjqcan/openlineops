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
    public async Task EmptyActiveRunQueriesAcceptAnUnspecifiedLineFilter()
    {
        await using var database = await TemporaryPostgresSchema.CreateAsync(
            fixture.ConnectionString);
        using var store = new PostgreSqlProductionCoordinationStore(database.ConnectionString);

        Assert.Empty(await store.ListRecoverableAsync());
        Assert.Empty(await store.ListActiveAsync());
    }

    [PostgresIntegrationFact]
    public async Task TerminalRunPaginationUsesStableBoundedCursorAndExcludesActiveRuns()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var materials = new PostgreSqlProductionMaterialRepository(fixture.ConnectionString);
        using var store = new PostgreSqlProductionCoordinationStore(fixture.ConnectionString);
        var firstTerminal = await AddRunAsync(
            store,
            materials,
            $"{suffix}-terminal-a",
            Now.AddYears(1));
        var secondTerminal = await AddRunAsync(
            store,
            materials,
            $"{suffix}-terminal-b",
            Now.AddYears(1).AddSeconds(10));
        var active = await AddRunAsync(
            store,
            materials,
            $"{suffix}-active",
            null);

        var seen = new HashSet<ProductionRunId>();
        ProductionRunTerminalCursor? cursor = null;
        ProductionRunTerminalCursor? previous = null;
        for (var pageIndex = 0; pageIndex < 10000; pageIndex++)
        {
            var page = await store.ListTerminalAsync(
                new ProductionRunTerminalPageRequest(1, cursor));
            Assert.InRange(page.Items.Count, 0, 1);
            foreach (var entry in page.Items)
            {
                Assert.NotNull(entry.Run.CompletedAtUtc);
                Assert.True(seen.Add(entry.Run.RunId));
                if (previous is not null)
                {
                    Assert.True(entry.Run.LastTransitionAtUtc > previous.LastTransitionAtUtc
                        || entry.Run.LastTransitionAtUtc == previous.LastTransitionAtUtc
                        && entry.Run.RunId.Value.CompareTo(previous.RunId.Value) > 0);
                }

                previous = new ProductionRunTerminalCursor(
                    entry.Run.LastTransitionAtUtc,
                    entry.Run.RunId);
            }

            if (page.Next is null)
            {
                break;
            }

            cursor = page.Next;
            if (pageIndex == 9999)
            {
                throw new InvalidDataException("Terminal Production Run pagination did not terminate.");
            }
        }

        Assert.Contains(firstTerminal.Id, seen);
        Assert.Contains(secondTerminal.Id, seen);
        Assert.DoesNotContain(active.Id, seen);
    }

    [PostgresIntegrationFact]
    public async Task ProtectedTransitionRollsBackRunAndEveryHoldWhenOneFenceWasReassigned()
    {
        await using var database = await TemporaryPostgresSchema.CreateAsync(
            fixture.ConnectionString);
        var suffix = $"atomic-hold-{Guid.NewGuid():N}";
        var station = new ResourceRequirement(ResourceKind.Station, $"station-system-{suffix}");
        var device = new ResourceRequirement(ResourceKind.Device, $"device-{suffix}");
        var operationPlan = OperationPlan(suffix, [station, device]);
        var run = ProductionRun.Create(
            ProductionRunId.New(),
            $"project-{suffix}",
            $"application-{suffix}",
            $"snapshot-{suffix}",
            $"topology-{suffix}",
            $"line-{suffix}",
            ProductionUnitId.New(),
            new ProductionUnitIdentity($"product-{suffix}", "serialNumber", $"SN-{suffix}"),
            null,
            null,
            $"operator-{suffix}",
            operationPlan.Definition.OperationId,
            Now,
            [operationPlan.Definition],
            [
                new RouteTransitionDefinition(
                    $"route-{suffix}-completed",
                    operationPlan.Definition.OperationId,
                    null,
                    RuntimeRouteTransitionKind.Sequence,
                    terminalDisposition: ProductDisposition.Completed)
            ]);
        using var store = new PostgreSqlProductionCoordinationStore(database.ConnectionString);
        using var materials = new PostgreSqlProductionMaterialRepository(database.ConnectionString);
        Assert.True(await materials.TryAddAsync(ProductionUnit.Register(
            run.ProductionUnitId,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            null,
            run.ActorId,
            Now.AddTicks(-1))));
        var unit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await materials.GetProductionUnitAsync(run.ProductionUnitId));
        Assert.True(await store.TryAddAsync(
            run,
            new ProductionRunExecutionPlan(run.Id, [operationPlan]),
            new ProductionRunAdmission(unit.Aggregate.ToSnapshot(), unit.Revision)));
        Assert.True(run.Start(Now.AddSeconds(1)).Succeeded);
        var revision = await store.SaveAsync(run, 0);
        var operation = Assert.Single(run.Operations);
        var leases = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await store.TryAcquireAsync(
                run.Id,
                operation.OperationRunId,
                operation.ResourceRequirements,
                TimeSpan.FromMinutes(5)));
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            RuntimeSessionId.New(),
            leases,
            Now.AddSeconds(2)).Succeeded);
        revision = await store.SaveAsync(run, revision);

        var deviceLease = Assert.Single(leases, lease => lease.Resource == device);
        await ExpireLeaseAsync(database.ConnectionString, device);
        var replacementRunId = ProductionRunId.New();
        var replacement = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await store.TryAcquireAsync(
                replacementRunId,
                $"operation-replacement-{suffix}@0001",
                [device],
                TimeSpan.FromMinutes(5))));
        Assert.True(run.RequestCancel("Operator canceled during fence reassignment.", Now.AddSeconds(3)).Succeeded);
        var hold = new ProductionRunLeaseHold(
            operation.OperationRunId,
            leases.Select(ResourceLeaseHoldClaim.FromLease).ToArray());

        await Assert.ThrowsAsync<ResourceLeaseOwnershipException>(() =>
            store.SaveWithLeaseHoldsAsync(run, revision, [hold]).AsTask());

        var persisted = Assert.IsType<ProductionRunPersistenceEntry>(await store.GetByIdAsync(run.Id));
        Assert.Equal(revision, persisted.Revision);
        Assert.Equal(ProductionRunControlState.Active, persisted.Run.ControlState);
        Assert.Null(persisted.Run.FailureCode);
        var stationLease = Assert.Single(
            await store.ListAsync(),
            lease => lease.Resource == station);
        Assert.Equal(run.Id, stationLease.ProductionRunId);
        Assert.NotEqual(DateTimeOffset.MaxValue, stationLease.ExpiresAtUtc);
        var persistedReplacement = Assert.Single(
            await store.ListAsync(),
            lease => lease.Resource == device);
        Assert.Equal(replacementRunId, persistedReplacement.ProductionRunId);
        Assert.Equal(replacement.FencingToken, persistedReplacement.FencingToken);

        await store.ReleaseAsync(
            run.Id,
            operation.OperationRunId,
            leases.Where(lease => lease.Resource == station)
                .Select(ResourceLeaseReleaseClaim.FromLease)
                .ToArray());
        await store.ReleaseAsync(
            replacementRunId,
            replacement.OperationRunId,
            [ResourceLeaseReleaseClaim.FromLease(replacement)]);
    }

    [PostgresIntegrationFact]
    public async Task ProtectedTransitionColdRestartPreservesOnlyRunningRecoveryHolds()
    {
        await using var database = await TemporaryPostgresSchema.CreateAsync(
            fixture.ConnectionString);
        var suffix = $"atomic-cold-{Guid.NewGuid():N}";
        var runningStation = new ResourceRequirement(
            ResourceKind.Station,
            $"station-system-{suffix}-running");
        var runningDevice = new ResourceRequirement(
            ResourceKind.Device,
            $"device-{suffix}-running");
        var pendingStation = new ResourceRequirement(
            ResourceKind.Station,
            $"station-system-{suffix}-pending");
        var entryPlan = OperationPlan($"{suffix}-entry");
        var runningPlan = OperationPlan(
            $"{suffix}-running",
            [runningStation, runningDevice]);
        var pendingPlan = OperationPlan($"{suffix}-pending", [pendingStation]);
        var joinPlan = OperationPlan($"{suffix}-join");
        var run = ProductionRun.Create(
            ProductionRunId.New(),
            $"project-{suffix}",
            $"application-{suffix}",
            $"snapshot-{suffix}",
            $"topology-{suffix}",
            $"line-{suffix}",
            ProductionUnitId.New(),
            new ProductionUnitIdentity($"product-{suffix}", "serialNumber", $"SN-{suffix}"),
            null,
            null,
            $"operator-{suffix}",
            entryPlan.Definition.OperationId,
            Now,
            [
                entryPlan.Definition,
                runningPlan.Definition,
                pendingPlan.Definition,
                joinPlan.Definition
            ],
            [
                new RouteTransitionDefinition(
                    $"route-{suffix}-fork-running",
                    entryPlan.Definition.OperationId,
                    runningPlan.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelFork,
                    parallelGroupId: $"parallel-{suffix}"),
                new RouteTransitionDefinition(
                    $"route-{suffix}-fork-pending",
                    entryPlan.Definition.OperationId,
                    pendingPlan.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelFork,
                    parallelGroupId: $"parallel-{suffix}"),
                new RouteTransitionDefinition(
                    $"route-{suffix}-join-running",
                    runningPlan.Definition.OperationId,
                    joinPlan.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelJoin,
                    parallelGroupId: $"parallel-{suffix}"),
                new RouteTransitionDefinition(
                    $"route-{suffix}-join-pending",
                    pendingPlan.Definition.OperationId,
                    joinPlan.Definition.OperationId,
                    RuntimeRouteTransitionKind.ParallelJoin,
                    parallelGroupId: $"parallel-{suffix}"),
                new RouteTransitionDefinition(
                    $"route-{suffix}-completed",
                    joinPlan.Definition.OperationId,
                    null,
                    RuntimeRouteTransitionKind.Sequence,
                    terminalDisposition: ProductDisposition.Completed)
            ]);
        IReadOnlyCollection<ResourceLease> runningLeases;
        ResourceLease pendingReservation;
        ProductionRunLeaseHold runningHold;
        long protectedRevision;
        using (var store = new PostgreSqlProductionCoordinationStore(database.ConnectionString))
        using (var materials = new PostgreSqlProductionMaterialRepository(database.ConnectionString))
        {
            Assert.True(await materials.TryAddAsync(ProductionUnit.Register(
                run.ProductionUnitId,
                run.ProductionUnitIdentity.ModelId,
                run.ProductionUnitIdentity.InputKey,
                run.ProductionUnitIdentity.Value,
                null,
                run.ActorId,
                Now.AddTicks(-1))));
            var unit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
                await materials.GetProductionUnitAsync(run.ProductionUnitId));
            Assert.True(await store.TryAddAsync(
                run,
                new ProductionRunExecutionPlan(
                    run.Id,
                    [entryPlan, runningPlan, pendingPlan, joinPlan]),
                new ProductionRunAdmission(unit.Aggregate.ToSnapshot(), unit.Revision)));
            Assert.True(run.Start(Now.AddSeconds(1)).Succeeded);
            var revision = await store.SaveAsync(run, 0);
            var entry = Assert.Single(run.Operations);
            var entryLeases = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
                await store.TryAcquireAsync(
                    run.Id,
                    entry.OperationRunId,
                    entry.ResourceRequirements,
                    TimeSpan.FromMinutes(5)));
            var entrySessionId = RuntimeSessionId.New();
            Assert.True(run.StartOperation(
                entry.OperationRunId,
                entrySessionId,
                entryLeases,
                Now.AddSeconds(2)).Succeeded);
            revision = await store.SaveAsync(run, revision);
            var entrySnapshot = run.ToSnapshot().Operations.Single(operation => string.Equals(
                operation.OperationRunId,
                entry.OperationRunId,
                StringComparison.Ordinal));
            var entryDispatch = new StationOperationDispatchRequest(
                run.ToSnapshot(),
                entrySnapshot,
                entryPlan,
                entrySessionId,
                new Dictionary<string, ProductionContextValue>(),
                entryLeases);
            var entryCompletedAtUtc = Now.AddSeconds(3);
            var entryCompletion = Completion(
                JobRequest(run, entrySnapshot, entryLeases, $"{suffix}-entry"),
                ResultJudgement.NotApplicable) with
            {
                CompletedAtUtc = entryCompletedAtUtc
            };
            Assert.True(run.CompleteOperation(
                entry.OperationRunId,
                ResultJudgement.NotApplicable,
                null,
                0,
                0,
                0,
                entryCompletedAtUtc,
                OperationExecutionEvidenceFactory.FromStationCompletion(
                    entryDispatch,
                    entryCompletion)).Succeeded);
            revision = await store.SaveAsync(run, revision);
            await store.ReleaseAsync(
                run.Id,
                entry.OperationRunId,
                entryLeases.Select(ResourceLeaseReleaseClaim.FromLease).ToArray());

            var running = run.Operations.Single(operation => string.Equals(
                operation.OperationId,
                runningPlan.Definition.OperationId,
                StringComparison.Ordinal));
            runningLeases = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
                await store.TryAcquireAsync(
                    run.Id,
                    running.OperationRunId,
                    running.ResourceRequirements,
                    TimeSpan.FromMinutes(5)));
            Assert.True(run.StartOperation(
                running.OperationRunId,
                RuntimeSessionId.New(),
                runningLeases,
                Now.AddSeconds(4)).Succeeded);
            revision = await store.SaveAsync(run, revision);

            var pending = run.Operations.Single(operation => string.Equals(
                operation.OperationId,
                pendingPlan.Definition.OperationId,
                StringComparison.Ordinal));
            Assert.Equal(ExecutionStatus.Pending, pending.ExecutionStatus);
            pendingReservation = Assert.Single(
                Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
                    await store.TryAcquireAsync(
                        run.Id,
                        pending.OperationRunId,
                        pending.ResourceRequirements,
                        TimeSpan.FromMinutes(5))));
            Assert.NotEqual(DateTimeOffset.MaxValue, pendingReservation.ExpiresAtUtc);

            Assert.True(run.MarkRecoveryRequired(
                "The running Station result boundary became uncertain.",
                Now.AddSeconds(5)).Succeeded);
            runningHold = new ProductionRunLeaseHold(
                running.OperationRunId,
                runningLeases.Select(ResourceLeaseHoldClaim.FromLease).ToArray());
            protectedRevision = await store.SaveWithLeaseHoldsAsync(
                run,
                revision,
                [runningHold]);
        }

        using var restarted = new PostgreSqlProductionCoordinationStore(database.ConnectionString);
        var restored = Assert.IsType<ProductionRunPersistenceEntry>(
            await restarted.GetByIdAsync(run.Id));
        Assert.Equal(protectedRevision, restored.Revision);
        Assert.Equal(ProductionRunControlState.RecoveryRequired, restored.Run.ControlState);
        var persistedLeases = await restarted.ListAsync();
        var held = persistedLeases
            .Where(lease => string.Equals(
                lease.OperationRunId,
                runningHold.OperationRunId,
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(runningLeases.Count, held.Length);
        Assert.All(held, static lease =>
            Assert.Equal(DateTimeOffset.MaxValue, lease.ExpiresAtUtc));
        var finitePending = Assert.Single(persistedLeases, lease => string.Equals(
            lease.OperationRunId,
            pendingReservation.OperationRunId,
            StringComparison.Ordinal));
        Assert.Equal(pendingReservation.FencingToken, finitePending.FencingToken);
        Assert.Equal(pendingReservation.ExpiresAtUtc, finitePending.ExpiresAtUtc);

        await restarted.ReleaseAsync(
            run.Id,
            runningHold.OperationRunId,
            runningLeases.Select(ResourceLeaseReleaseClaim.FromLease).ToArray());
        Assert.All(
            (await restarted.ListAsync()).Where(lease => string.Equals(
                lease.OperationRunId,
                runningHold.OperationRunId,
                StringComparison.Ordinal)),
            static lease => Assert.Equal(DateTimeOffset.MaxValue, lease.ExpiresAtUtc));

        await restarted.ReleaseRecoveryHoldAsync(run.Id, [runningHold]);
        var afterRecoveryRelease = await restarted.ListAsync();
        Assert.DoesNotContain(afterRecoveryRelease, lease => string.Equals(
            lease.OperationRunId,
            runningHold.OperationRunId,
            StringComparison.Ordinal));
        Assert.Equal(pendingReservation, Assert.Single(afterRecoveryRelease));
        await restarted.ReleaseAsync(
            run.Id,
            pendingReservation.OperationRunId,
            [ResourceLeaseReleaseClaim.FromLease(pendingReservation)]);
    }

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

    private static OperationExecutionPlan OperationPlan(
        string suffix,
        IEnumerable<ResourceRequirement>? resources = null)
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
            process,
            [],
            resources);
    }

    private static async ValueTask ExpireLeaseAsync(
        string connectionString,
        ResourceRequirement resource)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE olo_resource_leases
            SET expires_at_utc = clock_timestamp() - interval '1 second'
            WHERE resource_kind = @resource_kind
              AND resource_id = @resource_id;
            """;
        command.Parameters.AddWithValue("resource_kind", resource.Kind.ToString());
        command.Parameters.AddWithValue("resource_id", resource.ResourceId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class TemporaryPostgresSchema : IAsyncDisposable
    {
        private readonly string _administrativeConnectionString;
        private readonly string _schemaName;

        private TemporaryPostgresSchema(
            string administrativeConnectionString,
            string schemaName,
            string connectionString)
        {
            _administrativeConnectionString = administrativeConnectionString;
            _schemaName = schemaName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async ValueTask<TemporaryPostgresSchema> CreateAsync(
            string administrativeConnectionString)
        {
            var schemaName = $"olo_test_{Guid.NewGuid():N}";
            await using var connection = new Npgsql.NpgsqlConnection(
                administrativeConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA {schemaName};";
            await command.ExecuteNonQueryAsync();
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(
                administrativeConnectionString)
            {
                SearchPath = schemaName
            };
            return new TemporaryPostgresSchema(
                administrativeConnectionString,
                schemaName,
                builder.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new Npgsql.NpgsqlConnection(
                _administrativeConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA {_schemaName} CASCADE;";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<ProductionRun> AddRunAsync(
        PostgreSqlProductionCoordinationStore store,
        PostgreSqlProductionMaterialRepository materials,
        string suffix,
        DateTimeOffset? completedAtUtc)
    {
        var operationPlan = OperationPlan(suffix);
        var run = ProductionRun.Create(
            ProductionRunId.New(),
            $"project-{suffix}",
            $"application-{suffix}",
            $"snapshot-{suffix}",
            $"topology-{suffix}",
            $"line-{suffix}",
            ProductionUnitId.New(),
            new ProductionUnitIdentity($"product-{suffix}", "serialNumber", $"SN-{suffix}"),
            null,
            null,
            $"operator-{suffix}",
            operationPlan.Definition.OperationId,
            completedAtUtc ?? Now,
            [operationPlan.Definition],
            [
                new RouteTransitionDefinition(
                    $"route-{suffix}-completed",
                    operationPlan.Definition.OperationId,
                    null,
                    RuntimeRouteTransitionKind.Sequence,
                    terminalDisposition: ProductDisposition.Completed)
            ]);
        Assert.True(await materials.TryAddAsync(ProductionUnit.Register(
            run.ProductionUnitId,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            null,
            run.ActorId,
            run.CreatedAtUtc.AddTicks(-1))));
        var unit = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
            await materials.GetProductionUnitAsync(run.ProductionUnitId));
        Assert.True(await store.TryAddAsync(
            run,
            new ProductionRunExecutionPlan(run.Id, [operationPlan]),
            new ProductionRunAdmission(unit.Aggregate.ToSnapshot(), unit.Revision)));
        if (completedAtUtc is null)
        {
            return run;
        }

        Assert.True(run.Start(completedAtUtc.Value).Succeeded);
        var operation = Assert.Single(run.Operations);
        var leases = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await store.TryAcquireAsync(
                run.Id,
                operation.OperationRunId,
                operation.ResourceRequirements,
                TimeSpan.FromMinutes(5)));
        var runtimeSessionId = RuntimeSessionId.New();
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            runtimeSessionId,
            leases,
            completedAtUtc.Value).Succeeded);
        var terminalAtUtc = completedAtUtc.Value.AddSeconds(1);
        var runningSnapshot = run.ToSnapshot();
        var runningOperation = Assert.Single(runningSnapshot.Operations);
        var dispatch = new StationOperationDispatchRequest(
            runningSnapshot,
            runningOperation,
            operationPlan,
            runtimeSessionId,
            new Dictionary<string, ProductionContextValue>(),
            leases);
        var completion = Completion(
            JobRequest(run, runningOperation, leases, suffix),
            ResultJudgement.Passed) with
        {
            CompletedAtUtc = terminalAtUtc
        };
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Passed,
            null,
            0,
            0,
            0,
            terminalAtUtc,
            OperationExecutionEvidenceFactory.FromStationCompletion(dispatch, completion)).Succeeded);
        Assert.Equal(1, await store.SaveAsync(run, 0));
        await store.ReleaseAsync(
            run.Id,
            operation.OperationRunId,
            leases.Select(ResourceLeaseReleaseClaim.FromLease).ToArray());
        return run;
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
