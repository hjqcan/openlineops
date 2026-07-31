using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Processes;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Targets;
using OpenLineOps.Runtime.Infrastructure.Persistence;
using OpenLineOps.Traceability.Api.RuntimeIntegration;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Domain.Records;
using OpenLineOps.Traceability.Infrastructure.Persistence;
using OpenLineOps.Traceability.Infrastructure.Time;
using RuntimeStationId = OpenLineOps.Runtime.Domain.Identifiers.StationId;
using StoredTraceRecordId = OpenLineOps.Traceability.Domain.Identifiers.TraceRecordId;

namespace OpenLineOps.Api.Tests;

public sealed class ProductionRunTraceDomainEventSubscriberTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LostTraceProjectionRebuildsFromTerminalRunAndDurableEvidenceIdempotently()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-trace-rebuild-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var coordinationConnectionString =
            $"Data Source={Path.Combine(root, "coordination.sqlite")};Mode=ReadWriteCreate;Cache=Shared";
        var originalTraceConnectionString =
            $"Data Source={Path.Combine(root, "trace-original.sqlite")};Mode=ReadWriteCreate;Cache=Shared";
        var rebuiltTraceConnectionString =
            $"Data Source={Path.Combine(root, "trace-rebuilt.sqlite")};Mode=ReadWriteCreate;Cache=Shared";
        try
        {
            ProductionRunId runId;
            ProductionUnitId productionUnitId;
            string carrierId;
            int frozenMaterialEvidenceCount;
            JsonElement originalTraceJson;
            using (var runtimeRepository = new SqliteRuntimeSessionRepository(
                       coordinationConnectionString))
            using (var materials = new SqliteProductionMaterialRepository(
                       coordinationConnectionString))
            using (var runs = new SqliteProductionRunRepository(coordinationConnectionString))
            using (var originalTraceRepository = new SqliteTraceRecordRepository(
                       originalTraceConnectionString))
            {
                var run = CreateRun();
                runId = run.Id;
                productionUnitId = run.ProductionUnitId;
                carrierId = run.CarrierId!;
                var unit = ProductionUnit.Register(
                    run.ProductionUnitId,
                    run.ProductionUnitIdentity.ModelId,
                    run.ProductionUnitIdentity.InputKey,
                    run.ProductionUnitIdentity.Value,
                    new ProductionLotId(run.LotId!),
                    run.ActorId,
                    BaseTimeUtc.AddTicks(-1));
                Assert.True(await materials.TryAddAsync(unit));
                var unitEntry = Assert.IsType<ProductionMaterialPersistenceEntry<ProductionUnit>>(
                    await materials.GetProductionUnitAsync(unit.Id));
                Assert.True(await runs.TryAddAsync(
                    run,
                    CreateExecutionPlan(run),
                    new ProductionRunAdmission(
                        unitEntry.Aggregate.ToSnapshot(),
                        unitEntry.Revision)));

                var operation = StartOperation(run, out var sessionId);
                var session = CreateRunningSession(run, operation, sessionId);
                var command = StartCommand(session);
                Assert.True(session.CompleteCommand(
                    command.Id,
                    "{\"outcome\":\"Passed\"}",
                    BaseTimeUtc.AddSeconds(4),
                    ResultJudgement.Passed).Succeeded);
                Assert.True(session.CompleteStep(
                    command.StepId,
                    BaseTimeUtc.AddSeconds(5)).Succeeded);
                Assert.True(session.Complete(BaseTimeUtc.AddSeconds(6)).Succeeded);
                await runtimeRepository.SaveAsync(session, session.DomainEvents.ToArray());
                Assert.True(run.CompleteOperation(
                    operation.OperationRunId,
                    ResultJudgement.Passed,
                    new Dictionary<string, ProductionContextValue>
                    {
                        ["test.outcome"] = new(ProductionContextValueKind.Text, "Passed")
                    },
                    1,
                    1,
                    0,
                    session.CompletedAtUtc!.Value,
                    FreezeSessionEvidence(run, operation, session)).Succeeded);
                Assert.Equal(1, await runs.SaveAsync(run, 0));

                var originalSubscriber = new ProductionRunTraceDomainEventSubscriber(
                    new TraceRecordService(originalTraceRepository, new SystemClock()));
                var terminalEvidence = Assert.Single(
                    await runs.ListPendingTerminalOutboxAsync(1)).Evidence;
                frozenMaterialEvidenceCount = terminalEvidence.MaterialTimeline.Count;
                Assert.Equal(
                    TraceRecordProjectionOutcome.Created,
                    await originalSubscriber.ProjectAsync(terminalEvidence, default));
                var originalTrace = Assert.IsType<TraceRecord>(
                    await originalTraceRepository.GetByIdAsync(
                        new StoredTraceRecordId(run.Id.Value)));
                originalTraceJson = JsonSerializer.SerializeToElement(originalTrace);
            }

            using (var lateMaterials = new SqliteProductionMaterialRepository(
                       coordinationConnectionString))
            using (var lateRuns = new SqliteProductionRunRepository(
                       coordinationConnectionString))
            {
                var lateChild = ProductionUnit.Register(
                    ProductionUnitId.New(),
                    "product.late-component",
                    "serialNumber",
                    "LATE-COMPONENT-001",
                    null,
                    "operator.late-evidence",
                    BaseTimeUtc.AddSeconds(2));
                Assert.True(await lateMaterials.TryAddAsync(lateChild));
                var materialService = new ProductionMaterialService(lateMaterials, lateRuns);
                Assert.True((await materialService.LinkGenealogyAsync(
                    new LinkMaterialGenealogyCommand(
                        MaterialGenealogyLinkId.New(),
                        productionUnitId,
                        lateChild.Id,
                        "ComponentOf",
                        "operation.late-evidence",
                        "operator.late-evidence",
                        BaseTimeUtc.AddSeconds(3)))).Succeeded);
                var liveTimeline = await lateMaterials.ListTimelineAsync(
                    ProductionMaterialTimelineQuery.UnionScope(
                        productionUnitId,
                        runId,
                        new CarrierId(carrierId),
                        BaseTimeUtc.AddSeconds(6)));
                Assert.True(liveTimeline.Count > frozenMaterialEvidenceCount);
                Assert.Contains(
                    liveTimeline,
                    entry => entry.Kind == ProductionMaterialEvidenceKind.Genealogy
                        && entry.OccurredAtUtc < BaseTimeUtc.AddSeconds(6));
            }

            using var restartedRuns = new SqliteProductionRunRepository(
                coordinationConnectionString);
            using var rebuiltTraceRepository = new SqliteTraceRecordRepository(
                rebuiltTraceConnectionString);
            var rebuiltSubscriber = new ProductionRunTraceDomainEventSubscriber(
                new TraceRecordService(rebuiltTraceRepository, new SystemClock()));
            var rebuilder = new TraceProjectionRebuilder(restartedRuns, rebuiltSubscriber);

            var firstRebuild = await rebuilder.RebuildAsync(1);
            Assert.Equal(1, firstRebuild.ScannedRunCount);
            Assert.Equal(1, firstRebuild.CreatedRecordCount);
            Assert.Equal(0, firstRebuild.ExistingRecordCount);
            var rebuiltTrace = Assert.IsType<TraceRecord>(
                await rebuiltTraceRepository.GetByIdAsync(
                    new StoredTraceRecordId(runId.Value)));
            Assert.True(JsonElement.DeepEquals(
                originalTraceJson,
                JsonSerializer.SerializeToElement(rebuiltTrace)));

            var repeatedRebuild = await rebuilder.RebuildAsync(1);
            Assert.Equal(1, repeatedRebuild.ScannedRunCount);
            Assert.Equal(0, repeatedRebuild.CreatedRecordCount);
            Assert.Equal(1, repeatedRebuild.ExistingRecordCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException) when (attempt < 9)
                {
                    await Task.Delay(50);
                }
            }
        }
    }

    [Fact]
    public async Task ProductFailureKeepsCompletedExecutionAndNonconformingDisposition()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        var command = StartCommand(session);
        Assert.True(session.CompleteCommand(
            command.Id,
            "{\"outcome\":\"Failed\"}",
            BaseTimeUtc.AddSeconds(4),
            ResultJudgement.Failed).Succeeded);
        Assert.True(session.CompleteStep(command.StepId, BaseTimeUtc.AddSeconds(5)).Succeeded);
        Assert.True(session.Complete(BaseTimeUtc.AddSeconds(6)).Succeeded);
        await runtimeRepository.SaveAsync(session, session.DomainEvents.ToArray());
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Failed,
            new Dictionary<string, ProductionContextValue>
            {
                ["test.outcome"] = new(ProductionContextValueKind.Text, "Failed")
            },
            1,
            1,
            0,
            session.CompletedAtUtc!.Value,
            FreezeSessionEvidence(run, operation, session)).Succeeded);

        await subscriber.HandleAsync(TerminalEvidence(run));
        await subscriber.HandleAsync(TerminalEvidence(run));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        Assert.Equal(ExecutionStatus.Completed, trace.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, trace.Judgement);
        Assert.Equal(ProductDisposition.Nonconforming, trace.Disposition);
        var routeDecision = Assert.Single(trace.RouteDecisions);
        Assert.Null(routeDecision.TargetOperationId);
        Assert.Equal(ProductDisposition.Nonconforming, routeDecision.TerminalDisposition);
        var storedOperation = Assert.Single(trace.Operations);
        Assert.Equal(ExecutionStatus.Completed, storedOperation.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, storedOperation.Judgement);
        Assert.Empty(storedOperation.Incidents);
        Assert.Single(storedOperation.Outputs);
        var storedCommand = Assert.Single(storedOperation.Commands);
        Assert.Equal(ExecutionStatus.Completed, storedCommand.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, storedCommand.ResultJudgement);
        Assert.False(Assert.Single(storedOperation.Measurements).Passed);
        Assert.Equal(1, traceRepository.AddCount);
    }

    [Fact]
    public async Task MissingRouteFailsClosedAndPersistsTraceFailureEvidence()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun(includeTerminalRoutes: false);
        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        var command = StartCommand(session);
        Assert.True(session.CompleteCommand(
            command.Id,
            "{\"outcome\":\"Passed\"}",
            BaseTimeUtc.AddSeconds(4),
            ResultJudgement.Passed).Succeeded);
        Assert.True(session.CompleteStep(command.StepId, BaseTimeUtc.AddSeconds(5)).Succeeded);
        Assert.True(session.Complete(BaseTimeUtc.AddSeconds(6)).Succeeded);
        await runtimeRepository.SaveAsync(session, session.DomainEvents.ToArray());
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Passed,
            new Dictionary<string, ProductionContextValue>
            {
                ["test.outcome"] = new(ProductionContextValueKind.Text, "Passed")
            },
            1,
            1,
            0,
            session.CompletedAtUtc!.Value,
            FreezeSessionEvidence(run, operation, session)).Succeeded);

        await subscriber.HandleAsync(TerminalEvidence(run));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        Assert.Equal(ExecutionStatus.Failed, trace.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, trace.Judgement);
        Assert.Equal(ProductDisposition.Held, trace.Disposition);
        Assert.Equal("Runtime.RouteResolutionFailed", trace.FailureCode);
        Assert.Contains("Route resolution failed after Operation Run", trace.FailureReason, StringComparison.Ordinal);
        Assert.Empty(trace.RouteDecisions);
        var storedOperation = Assert.Single(trace.Operations);
        Assert.Equal(ExecutionStatus.Completed, storedOperation.ExecutionStatus);
        Assert.Equal(ResultJudgement.Passed, storedOperation.Judgement);
    }

    [Fact]
    public async Task SystemFailureKeepsUnknownJudgementAndIncident()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        var command = StartCommand(session);
        Assert.True(session.FailCommand(
            command.Id,
            "Vendor process exited with code 7.",
            BaseTimeUtc.AddSeconds(4)).Succeeded);
        Assert.True(session.FailStep(
            command.StepId,
            "Vendor process exited with code 7.",
            BaseTimeUtc.AddSeconds(4)).Succeeded);
        Assert.True(session.Fail(
            BaseTimeUtc.AddSeconds(5),
            "Vendor.ProcessCrashed",
            "Vendor process exited with code 7.").Succeeded);
        await runtimeRepository.SaveAsync(session, session.DomainEvents.ToArray());
        Assert.True(run.FailOperation(
            operation.OperationRunId,
            ExecutionStatus.Failed,
            "Runtime.OperationSessionFailed",
            "Vendor process exited with code 7.",
            0,
            1,
            1,
            session.CompletedAtUtc!.Value,
            FreezeSessionEvidence(run, operation, session)).Succeeded);

        await subscriber.HandleAsync(TerminalEvidence(run));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        Assert.Equal(ExecutionStatus.Failed, trace.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, trace.Judgement);
        Assert.Equal(ProductDisposition.Held, trace.Disposition);
        var storedOperation = Assert.Single(trace.Operations);
        Assert.Equal(ExecutionStatus.Failed, storedOperation.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, storedOperation.Judgement);
        Assert.Single(storedOperation.Incidents);
        Assert.Equal(ExecutionStatus.Failed, Assert.Single(storedOperation.Commands).ExecutionStatus);
    }

    [Fact]
    public async Task CommandEvidenceFreezesExternalProgramArtifactHash()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        var command = StartCommand(session);
        var payload = RuntimeCommandEvidencePayload.Attach(
            "{\"outcome\":\"Passed\"}",
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            [
                new RuntimeCommandArtifactEvidence(
                    "vendor-report.pdf",
                    "Report",
                    $"external-programs/{run.Id.Value:N}/{command.Id.Value:N}/vendor-report.pdf",
                    "application/pdf",
                    512,
                    new string('a', 64))
            ]);
        Assert.True(session.CompleteCommand(
            command.Id,
            payload,
            BaseTimeUtc.AddSeconds(4),
            ResultJudgement.Passed).Succeeded);
        Assert.True(session.CompleteStep(command.StepId, BaseTimeUtc.AddSeconds(5)).Succeeded);
        Assert.True(session.Complete(BaseTimeUtc.AddSeconds(6)).Succeeded);
        await runtimeRepository.SaveAsync(session, session.DomainEvents.ToArray());
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Passed,
            null,
            1,
            1,
            0,
            session.CompletedAtUtc!.Value,
            FreezeSessionEvidence(run, operation, session)).Succeeded);

        await subscriber.HandleAsync(TerminalEvidence(run));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        var artifact = Assert.Single(Assert.Single(trace.Operations).Artifacts);
        Assert.Equal(ArtifactKind.Report, artifact.Kind);
        Assert.Equal("application/pdf", artifact.MediaType);
        Assert.Equal(new string('a', 64), artifact.Sha256);
        Assert.Contains(run.Id.Value.ToString("N"), artifact.StorageKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeDispatchCreatesHonestOperationWithoutSessionEvidence()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        Assert.True(run.Start(BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(run.Cancel("Operator stopped before dispatch.", BaseTimeUtc.AddSeconds(2)).Succeeded);

        await subscriber.HandleAsync(TerminalEvidence(run));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        Assert.Equal(ExecutionStatus.Canceled, trace.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, trace.Judgement);
        Assert.Equal(ProductDisposition.Held, trace.Disposition);
        var operation = Assert.Single(trace.Operations);
        Assert.Equal(ExecutionStatus.Canceled, operation.ExecutionStatus);
        Assert.Null(operation.RuntimeSessionId);
        Assert.Null(operation.StartedAtUtc);
        Assert.Empty(operation.Commands);
        Assert.Empty(operation.FencingTokens);
    }

    [Fact]
    public async Task CancellationDuringRuntimeSessionFreezesExactCanceledCommandEvidence()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        var command = StartCommand(session);
        const string reason = "Operator canceled the active vendor program.";
        var canceledAtUtc = BaseTimeUtc.AddSeconds(4);
        Assert.True(session.CancelCommand(
            command.Id,
            canceledAtUtc,
            reason).Succeeded);
        Assert.True(session.CancelStep(command.StepId, canceledAtUtc).Succeeded);
        Assert.True(session.Cancel(canceledAtUtc, reason).Succeeded);
        await runtimeRepository.SaveAsync(session, session.DomainEvents.ToArray());
        Assert.True(run.CancelOperation(
            operation.OperationRunId,
            "Runtime.OperationCanceled",
            reason,
            completedStepCount: 0,
            commandCount: 1,
            incidentCount: 0,
            canceledAtUtc: canceledAtUtc,
            executionEvidence: FreezeSessionEvidence(run, operation, session)).Succeeded);

        await subscriber.HandleAsync(TerminalEvidence(run));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        Assert.Equal(ExecutionStatus.Canceled, trace.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, trace.Judgement);
        Assert.Equal(ProductDisposition.Held, trace.Disposition);
        var storedOperation = Assert.Single(trace.Operations);
        Assert.Equal(ExecutionStatus.Canceled, storedOperation.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, storedOperation.Judgement);
        Assert.Equal("Runtime.OperationCanceled", storedOperation.FailureCode);
        Assert.Equal(TraceRuntimeSessionStatus.Canceled, storedOperation.RuntimeSessionStatus);
        Assert.Equal(0, storedOperation.CompletedStepCount);
        Assert.Equal(1, storedOperation.CommandCount);
        Assert.Equal(0, storedOperation.IncidentCount);
        var storedCommand = Assert.Single(storedOperation.Commands);
        Assert.Equal(ExecutionStatus.Canceled, storedCommand.ExecutionStatus);
        Assert.Equal(ResultJudgement.Aborted, storedCommand.ResultJudgement);
    }

    [Fact]
    public async Task SafeStopTracePreservesDurableRequestAndStationAcknowledgementEvidence()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        const string reason = "Guard opened during vendor execution.";
        Assert.True(run.RequestSafeStop(
            "operator.safety",
            reason,
            BaseTimeUtc.AddSeconds(3)).Succeeded);
        Assert.True(run.AcknowledgeSafeStop(BaseTimeUtc.AddSeconds(4)).Succeeded);
        Assert.True(session.Cancel(BaseTimeUtc.AddSeconds(5), reason).Succeeded);
        await runtimeRepository.SaveAsync(session, session.DomainEvents.ToArray());
        Assert.True(run.CancelOperation(
            operation.OperationRunId,
            "Runtime.OperationCanceled",
            reason,
            completedStepCount: 0,
            commandCount: 0,
            incidentCount: 0,
            canceledAtUtc: BaseTimeUtc.AddSeconds(5),
            executionEvidence: FreezeSessionEvidence(run, operation, session)).Succeeded);

        await subscriber.HandleAsync(TerminalEvidence(run));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        var requested = Assert.Single(trace.AuditEntries, entry =>
            entry.Action == "ProductionRun.SafeStop.Requested");
        Assert.Equal("operator.safety", requested.ActorId.Value);
        Assert.Contains(reason, requested.Detail, StringComparison.Ordinal);
        Assert.Equal(BaseTimeUtc.AddSeconds(3), requested.OccurredAtUtc);
        var acknowledged = Assert.Single(trace.AuditEntries, entry =>
            entry.Action == "ProductionRun.SafeStop.Acknowledged");
        Assert.Equal("system.station-safety", acknowledged.ActorId.Value);
        Assert.Equal(BaseTimeUtc.AddSeconds(4), acknowledged.OccurredAtUtc);
    }

    [Fact]
    public async Task ActiveScrapTracePreservesRequestedAndFinalizedEvidence()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        const string reason = "Board was physically damaged during active execution.";
        Assert.True(run.RequestScrap(
            "operator.scrap",
            reason,
            BaseTimeUtc.AddSeconds(3)).Succeeded);
        Assert.True(session.Cancel(BaseTimeUtc.AddSeconds(5), reason).Succeeded);
        await runtimeRepository.SaveAsync(session, session.DomainEvents.ToArray());
        Assert.True(run.RecordOperationCancellation(
            operation.OperationRunId,
            "Runtime.OperationCanceled",
            reason,
            completedStepCount: 0,
            commandCount: 0,
            incidentCount: 0,
            canceledAtUtc: BaseTimeUtc.AddSeconds(5),
            executionEvidence: FreezeSessionEvidence(run, operation, session)).Succeeded);
        Assert.True(run.ResolveDispatchWave([operation.OperationRunId]).Succeeded);

        await subscriber.HandleAsync(TerminalEvidence(run));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        Assert.Equal(ExecutionStatus.Completed, trace.ExecutionStatus);
        Assert.Equal(ResultJudgement.Failed, trace.Judgement);
        Assert.Equal(ProductDisposition.Scrapped, trace.Disposition);
        Assert.Equal(ExecutionStatus.Canceled, Assert.Single(trace.Operations).ExecutionStatus);
        var requested = Assert.Single(trace.AuditEntries, entry =>
            entry.Action == "ProductionRun.Scrap.Requested");
        Assert.Equal("operator.scrap", requested.ActorId.Value);
        Assert.Contains(reason, requested.Detail, StringComparison.Ordinal);
        Assert.Equal(BaseTimeUtc.AddSeconds(3), requested.OccurredAtUtc);
        var finalized = Assert.Single(trace.AuditEntries, entry =>
            entry.Action == "ProductionRun.Scrap.Finalized");
        Assert.Equal("operator.scrap", finalized.ActorId.Value);
        Assert.Contains(operation.OperationRunId, finalized.Detail, StringComparison.Ordinal);
        Assert.Equal(BaseTimeUtc.AddSeconds(5), finalized.OccurredAtUtc);
    }

    [Fact]
    public async Task ReconciledInterruptedOperationFreezesOperatorEvidenceWithoutRuntimeReplay()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = CreateSubscriber(runtimeRepository, traceRepository);
        var run = CreateRun();
        var operation = StartOperation(run, out _);
        Assert.True(run.MarkRecoveryRequired(
            "Agent disappeared after device actuation.",
            BaseTimeUtc.AddSeconds(2)).Succeeded);
        var decision = new ProductionRecoveryDecision(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            ProductionRecoveryDecisionKind.Reconcile,
            "operator-board",
            "Inspection confirms the vendor test completed and passed.",
            "inspection:report-0042",
            BaseTimeUtc.AddSeconds(3),
            operationRunId: operation.OperationRunId,
            observedJudgement: ResultJudgement.Passed,
            observedOutputs: new Dictionary<string, ProductionContextValue>
            {
                ["test.outcome"] = new(ProductionContextValueKind.Text, "Passed")
            });
        Assert.True(run.ReconcileRecovery(decision).Succeeded);

        await subscriber.HandleAsync(TerminalEvidence(run));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        var storedOperation = Assert.Single(trace.Operations);
        Assert.Equal(TraceRuntimeSessionStatus.Reconciled, storedOperation.RuntimeSessionStatus);
        Assert.Equal(ResultJudgement.Passed, storedOperation.Judgement);
        Assert.Empty(storedOperation.Commands);
        Assert.Empty(storedOperation.Incidents);
        Assert.Equal("Passed", Assert.Single(storedOperation.Outputs).CanonicalJson.Trim('"'));
        var audit = Assert.Single(trace.AuditEntries, entry =>
            entry.Action == "ProductionRun.Recovery.Reconcile");
        Assert.Equal("operator-board", audit.ActorId.Value);
        Assert.Contains("inspection:report-0042", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteStationCompletionFreezesDetailedEvidenceAndCentralArtifactKey()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var stationJobs = new InMemoryStationJobCoordinationStore();
        var materials = new InMemoryProductionMaterialRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var service = new TraceRecordService(traceRepository, new SystemClock());
        var subscriber = new ProductionRunTraceDomainEventSubscriber(service);
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var stepId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var outputs = TypedOutputs("Passed");
        var request = await PrimeStationResultInboxAsync(
            stationJobs,
            run,
            operation,
            sessionId);
        var dispatchRequest = CreateDispatchRequest(run, operation, sessionId);
        var artifactReceipt = StationArtifactReceiptIdentity.Create(
            request.AgentId,
            request.StationId,
            request.JobId,
            "vendor-report.pdf",
            "Report",
            "application/pdf",
            512,
            new string('a', 64));
        var completion = new StationJobCompleted(
            Guid.NewGuid(),
            request.JobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            sessionId.Value,
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            outputs,
            1,
            1,
            0,
            [
                new StationJobStepEvidence(
                    stepId,
                    "node.board-test",
                    "action.board-test",
                    "System",
                    operation.StationSystemId,
                    "Execute vendor program",
                    "Completed",
                    BaseTimeUtc.AddSeconds(2),
                    BaseTimeUtc.AddSeconds(6),
                    null)
            ],
            [
                new StationJobCommandEvidence(
                    commandId,
                    stepId,
                    "node.board-test",
                    "action.board-test",
                    "System",
                    operation.StationSystemId,
                    "test.external",
                    "ExecuteProgram",
                    ExecutionStatus.Completed,
                    BaseTimeUtc.AddSeconds(2),
                    BaseTimeUtc.AddSeconds(32),
                    BaseTimeUtc.AddSeconds(2),
                    BaseTimeUtc.AddSeconds(3),
                    BaseTimeUtc.AddSeconds(6),
                    "{\"outcome\":\"Passed\"}",
                    null,
                    ResultJudgement.Passed)
            ],
            [],
            [
                new StationJobArtifact(
                    "vendor-report.pdf",
                    "Report",
                    artifactReceipt.StorageKey,
                    artifactReceipt.ReceiptId,
                    "application/pdf",
                    512,
                    new string('a', 64))
            ],
            null,
            null,
            BaseTimeUtc.AddSeconds(7));
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Passed,
            new Dictionary<string, ProductionContextValue>
            {
                ["test.outcome"] = new(ProductionContextValueKind.Text, "Passed")
            },
            1,
            1,
            0,
            completion.CompletedAtUtc,
            OperationExecutionEvidenceFactory.FromStationCompletion(
                dispatchRequest,
                completion)).Succeeded);
        await stationJobs.RecordCompletionAsync(completion);

        await subscriber.HandleAsync(TerminalEvidence(run));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        var storedOperation = Assert.Single(trace.Operations);
        Assert.Equal(sessionId.Value, storedOperation.RuntimeSessionId?.Value);
        Assert.Equal(commandId, Assert.Single(storedOperation.Commands).RuntimeCommandId.Value);
        var artifact = Assert.Single(storedOperation.Artifacts);
        Assert.Equal(artifactReceipt.StorageKey, artifact.StorageKey);
        Assert.Equal(new string('a', 64), artifact.Sha256);
    }

    [Fact]
    public async Task RemoteStationCompletionRejectsTamperedIdentityAndCounts()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var stationJobs = new InMemoryStationJobCoordinationStore();
        var materials = new InMemoryProductionMaterialRepository();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = new ProductionRunTraceDomainEventSubscriber(
            new TraceRecordService(traceRepository, new SystemClock()));
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var request = await PrimeStationResultInboxAsync(
            stationJobs,
            run,
            operation,
            sessionId);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await stationJobs.RecordCompletionAsync(new StationJobCompleted(
            Guid.NewGuid(),
            Guid.NewGuid(),
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            sessionId.Value,
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            TypedOutputs(),
            0,
            0,
            0,
            [],
            [],
            [],
            [],
            null,
            null,
            BaseTimeUtc.AddSeconds(7))));
        Assert.Contains("unknown job", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
    }

    [Fact]
    public async Task RemoteStationCompletionRejectsTamperedEvidenceCount()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var stationJobs = new InMemoryStationJobCoordinationStore();
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = new ProductionRunTraceDomainEventSubscriber(
            new TraceRecordService(traceRepository, new SystemClock()));
        var run = CreateRun();
        var operation = StartOperation(run, out var sessionId);
        var request = await PrimeStationResultInboxAsync(
            stationJobs,
            run,
            operation,
            sessionId);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await stationJobs.RecordCompletionAsync(new StationJobCompleted(
            Guid.NewGuid(),
            request.JobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            sessionId.Value,
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            TypedOutputs(),
            1,
            0,
            0,
            [],
            [],
            [],
            [],
            null,
            null,
            BaseTimeUtc.AddSeconds(7))));
        Assert.Contains("counts", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
    }

    [Fact]
    public async Task TerminalTraceIncludesUnitCarrierSlotDispositionAndGenealogyTimeline()
    {
        var runtimeRepository = new InMemoryRuntimeSessionRepository();
        var stationJobs = new InMemoryStationJobCoordinationStore();
        var materialStorage = new InMemoryProductionMaterialRepository();
        var materials = new RecordingProductionMaterialRepository(materialStorage);
        var materialService = new ProductionMaterialService(
            materials,
            new InMemoryProductionRunRepository(materialStorage));
        var traceRepository = new InMemoryTraceRecordRepository();
        var subscriber = new ProductionRunTraceDomainEventSubscriber(
            new TraceRecordService(traceRepository, new SystemClock()));
        var run = CreateRun();
        var parentId = OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New();
        var carrierId = new CarrierId(run.CarrierId!);
        var station = MaterialLocation.AtStation(
            run.ProductionLineDefinitionId,
            "station.functional-test");
        var slot = new SlotAddress(
            run.ProductionLineDefinitionId,
            "station.functional-test",
            "slot-01");
        Assert.True((await materialService.RegisterUnitAsync(new RegisterProductionUnitCommand(
            run.ProductionUnitId,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            null,
            run.ActorId,
            BaseTimeUtc.AddSeconds(-1)))).Succeeded);
        Assert.True((await materialService.RegisterUnitAsync(new RegisterProductionUnitCommand(
            parentId,
            run.ProductionUnitIdentity.ModelId,
            "serialNumber",
            "UNIT-PARENT-0001",
            null,
            run.ActorId,
            BaseTimeUtc.AddSeconds(-1)))).Succeeded);
        Assert.True((await materialService.RegisterCarrierAsync(new RegisterCarrierCommand(
            carrierId,
            "tray-4",
            4,
            run.ActorId,
            BaseTimeUtc.AddSeconds(-1)))).Succeeded);
        Assert.True((await materialService.RegisterSlotAsync(new RegisterSlotCommand(
            slot,
            "engineer-a",
            BaseTimeUtc.AddSeconds(-1)))).Succeeded);
        Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
            Guid.NewGuid(),
            MaterialReference.ForProductionUnit(run.ProductionUnitId),
            station,
            "scanner-a",
            BaseTimeUtc))).Succeeded);
        Assert.True((await materialService.ArriveAsync(new ArriveMaterialCommand(
            Guid.NewGuid(),
            MaterialReference.ForCarrier(carrierId),
            station,
            "scanner-a",
            BaseTimeUtc))).Succeeded);
        Assert.True((await materialService.HoldAsync(new HoldProductionUnitCommand(
            run.ProductionUnitId,
            "quality gate",
            "quality-a",
            BaseTimeUtc))).Succeeded);
        Assert.True((await materialService.ReleaseAsync(new ReleaseProductionUnitCommand(
            run.ProductionUnitId,
            "quality-a",
            BaseTimeUtc))).Succeeded);
        Assert.True((await materialService.ReserveSlotAsync(new ReserveSlotCommand(
            slot,
            MaterialReference.ForProductionUnit(run.ProductionUnitId),
            "coordinator-a",
            BaseTimeUtc))).Succeeded);
        Assert.True((await materialService.LinkGenealogyAsync(new LinkMaterialGenealogyCommand(
            MaterialGenealogyLinkId.New(),
            parentId,
            run.ProductionUnitId,
            "ComponentOf",
            "operation.board-test",
            "operator-a",
            BaseTimeUtc))).Succeeded);

        var operation = StartOperation(run, out var sessionId);
        var session = CreateRunningSession(run, operation, sessionId);
        var command = StartCommand(session);
        Assert.True(session.CompleteCommand(
            command.Id,
            "{\"outcome\":\"Passed\"}",
            BaseTimeUtc.AddSeconds(4),
            ResultJudgement.Passed).Succeeded);
        Assert.True(session.CompleteStep(command.StepId, BaseTimeUtc.AddSeconds(5)).Succeeded);
        Assert.True(session.Complete(BaseTimeUtc.AddSeconds(6)).Succeeded);
        await runtimeRepository.SaveAsync(session, session.DomainEvents.ToArray());
        Assert.True(run.CompleteOperation(
            operation.OperationRunId,
            ResultJudgement.Passed,
            null,
            1,
            1,
            0,
            session.CompletedAtUtc!.Value,
            FreezeSessionEvidence(run, operation, session)).Succeeded);

        var frozenTimeline = await materialStorage.ListTimelineAsync(
            ProductionMaterialTimelineQuery.UnionScope(
                run.ProductionUnitId,
                run.Id,
                new CarrierId(run.CarrierId!),
                run.CompletedAtUtc));
        materials.ResetTimelineQueries();
        await subscriber.HandleAsync(TerminalEvidence(run, frozenTimeline));

        var trace = Assert.IsType<TraceRecord>(
            await traceRepository.GetByIdAsync(new StoredTraceRecordId(run.Id.Value)));
        Assert.Single(trace.Genealogy);
        Assert.Equal(2, trace.MaterialLocationTransitions.Count);
        Assert.Single(trace.SlotOccupancyTransitions);
        Assert.Equal(2, trace.DispositionTransitions.Count);
        Assert.Contains(trace.MaterialLocationTransitions, transition =>
            transition.MaterialKind == "Carrier" && transition.MaterialId == carrierId.Value);
        Assert.Equal("Reserved", Assert.Single(trace.SlotOccupancyTransitions).CurrentStatus);
        Assert.Equal(0, materials.TimelineQueryCount);
        Assert.Null(materials.LastTimelineQuery);
    }

    private static JsonElement TypedOutputs(string? outcome = null)
    {
        var json = outcome is null
            ? "{}"
            : $"{{\"test.outcome\":{{\"kind\":\"Text\",\"value\":\"{outcome}\"}}}}";
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async ValueTask<StationJobRequested> PrimeStationResultInboxAsync(
        InMemoryStationJobCoordinationStore stationJobs,
        ProductionRun run,
        OperationRun operation,
        RuntimeSessionId sessionId)
    {
        using var inputs = JsonDocument.Parse("{}");
        var idempotencyKey = $"{run.Id.Value:D}/{operation.OperationRunId}";
        var request = new StationJobRequested(
            Guid.NewGuid(),
            StationJobIdentity.CreateJobId(idempotencyKey),
            idempotencyKey,
            "agent.functional-test",
            operation.StationId.Value,
            operation.StationSystemId,
            run.Id.Value,
            run.ProductionUnitId.Value,
            sessionId.Value,
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
            operation.OperationId,
            operation.ProcessDefinitionId.Value,
            operation.ProcessVersionId.Value,
            operation.ConfigurationSnapshotId.Value,
            operation.RecipeSnapshotId.Value,
            operation.ResourceRequirements.Select(resource => new StationResourceFence(
                    resource.Kind.ToString(),
                    resource.ResourceId,
                    operation.FencingTokens[resource],
                    BaseTimeUtc.AddMinutes(5)))
                .ToArray(),
            inputs.RootElement.Clone(),
            BaseTimeUtc.AddSeconds(1));
        var leaseChanges = request.ResourceFences
            .Select(fence => StationDispatchMessageIdentity.CreateLeaseGranted(request, fence))
            .ToArray();
        Assert.True(await stationJobs.TryEnqueueAsync(request, leaseChanges));
        await stationJobs.RecordAcceptedAsync(new StationJobAccepted(
            Guid.NewGuid(),
            request.JobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            BaseTimeUtc.AddSeconds(1)));
        return request;
    }

    private static ProductionRunTraceDomainEventSubscriber CreateSubscriber(
        InMemoryRuntimeSessionRepository runtimeRepository,
        InMemoryTraceRecordRepository traceRepository)
    {
        var service = new TraceRecordService(
            traceRepository,
            new SystemClock());
        return new ProductionRunTraceDomainEventSubscriber(service);
    }

    private static ProductionRun CreateRun(bool includeTerminalRoutes = true)
    {
        var operation = new OperationRunDefinition(
            "operation.board-test",
            "station.functional-test",
            new RuntimeStationId("station.functional-test"),
            new ProcessDefinitionId("process.board-test"),
            new ProcessVersionId("process.board-test@1.0.0"),
            new ConfigurationSnapshotId("configuration.board-test"),
            new RecipeSnapshotId("recipe.board-test"),
            [
                new ResourceRequirement(ResourceKind.Station, "station.functional-test"),
                new ResourceRequirement(ResourceKind.Fixture, "fixture.board-test"),
                new ResourceRequirement(ResourceKind.Device, "tester.board-test")
            ]);
        var routeTransitions = includeTerminalRoutes
            ? new RouteTransitionDefinition[]
            {
                new(
                    "operation.board-test.failed",
                    operation.OperationId,
                    null,
                    RuntimeRouteTransitionKind.Judgement,
                    ResultJudgement.Failed,
                    terminalDisposition: ProductDisposition.Nonconforming),
                new(
                    "operation.board-test.default",
                    operation.OperationId,
                    null,
                    RuntimeRouteTransitionKind.Sequence,
                    terminalDisposition: ProductDisposition.Completed)
            }
            : [];
        return ProductionRun.Create(
            ProductionRunId.New(),
            "project-board",
            "application-board-test",
            "snapshot-board-test",
            "topology-board-test",
            "line-board-test",
            OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New(),
            new ProductionUnitIdentity("board-model", "serialNumber", "UNIT-BOARD-0001"),
            "lot-board",
            "carrier-board",
            "operator-board",
            operation.OperationId,
            BaseTimeUtc,
            [operation],
            routeTransitions);
    }

    private static ProductionRunExecutionPlan CreateExecutionPlan(ProductionRun run)
    {
        var definition = Assert.Single(run.OperationDefinitions);
        var process = new ExecutableRuntimeProcess(
            definition.ProcessDefinitionId,
            definition.ProcessVersionId,
            []);
        var operation = new OperationExecutionPlan(
            definition.OperationId,
            definition.StationSystemId,
            definition.StationId,
            definition.ConfigurationSnapshotId,
            definition.RecipeSnapshotId,
            process,
            [],
            definition.ResourceRequirements);
        return new ProductionRunExecutionPlan(run.Id, [operation]);
    }

    private static OperationRun StartOperation(
        ProductionRun run,
        out RuntimeSessionId sessionId)
    {
        Assert.True(run.Start(BaseTimeUtc.AddSeconds(1)).Succeeded);
        var operation = Assert.Single(run.Operations);
        sessionId = RuntimeSessionId.New();
        var leases = operation.ResourceRequirements
            .Select((resource, index) => new ResourceLease(
                resource,
                run.Id,
                operation.OperationRunId,
                index + 1,
                BaseTimeUtc,
                BaseTimeUtc.AddMinutes(5)))
            .ToArray();
        Assert.True(run.StartOperation(
            operation.OperationRunId,
            sessionId,
            leases,
            BaseTimeUtc.AddSeconds(1)).Succeeded);
        return operation;
    }

    private static RuntimeSession CreateRunningSession(
        ProductionRun run,
        OperationRun operation,
        RuntimeSessionId sessionId)
    {
        var session = RuntimeSession.Create(
            sessionId,
            operation.StationId,
            operation.ProcessDefinitionId,
            operation.ProcessVersionId,
            operation.ConfigurationSnapshotId,
            operation.RecipeSnapshotId,
            BaseTimeUtc,
            new RuntimeSessionTraceMetadata(
                run.Id,
                run.ProductionUnitId,
                run.ProductionLineDefinitionId,
                operation.OperationId,
                operation.OperationRunId,
                operation.Attempt,
                operation.StationSystemId,
                run.ProductionUnitIdentity,
                run.LotId,
                run.CarrierId,
                "fixture.board-test",
                "tester.board-test",
                run.ActorId,
                run.ProjectId,
                run.ApplicationId,
                run.ProjectSnapshotId,
                run.TopologyId,
                operation.FencingTokens
                    .OrderBy(pair => pair.Key.CanonicalKey, StringComparer.Ordinal)
                    .Select(pair => new ResourceLeaseFenceEvidence(
                        pair.Key,
                        pair.Value,
                        BaseTimeUtc.AddHours(1)))));
        Assert.True(session.Start(BaseTimeUtc.AddSeconds(1)).Succeeded);
        return session;
    }

    private static OperationExecutionEvidence FreezeSessionEvidence(
        ProductionRun run,
        OperationRun operation,
        RuntimeSession session)
        => OperationExecutionEvidenceFactory.FromRuntimeSession(
            CreateDispatchRequest(run, operation, session.Id),
            session);

    private static StationOperationDispatchRequest CreateDispatchRequest(
        ProductionRun run,
        OperationRun operation,
        RuntimeSessionId sessionId)
    {
        var runSnapshot = run.ToSnapshot();
        var operationSnapshot = runSnapshot.Operations.Single(candidate =>
            string.Equals(
                candidate.OperationRunId,
                operation.OperationRunId,
                StringComparison.Ordinal));
        var executionPlan = CreateExecutionPlan(run).Operations.Single(candidate =>
            string.Equals(
                candidate.Definition.OperationId,
                operation.OperationId,
                StringComparison.Ordinal));
        var leases = operation.FencingTokens.Select(pair => new ResourceLease(
            pair.Key,
            run.Id,
            operation.OperationRunId,
            pair.Value,
            BaseTimeUtc,
            BaseTimeUtc.AddHours(1))).ToArray();
        return new StationOperationDispatchRequest(
            runSnapshot,
            operationSnapshot,
            executionPlan,
            sessionId,
            new Dictionary<string, ProductionContextValue>(),
            leases);
    }

    private static ProductionRunTerminalEvidence TerminalEvidence(
        ProductionRun run,
        IReadOnlyCollection<ProductionMaterialTimelineEntry>? materialTimeline = null) =>
        new(run.ToSnapshot(), materialTimeline ?? []);

    private static OpenLineOps.Runtime.Domain.Commands.RuntimeCommand StartCommand(RuntimeSession session)
    {
        var step = session.StartStep(
            RuntimeStepId.New(),
            new RuntimeNodeId("node.board-test"),
            "Execute vendor program",
            BaseTimeUtc.AddSeconds(2),
            new RuntimeActionId("action.board-test"),
            new RuntimeTargetReference(RuntimeTargetKinds.System, "station.functional-test"));
        var command = session.CreateCommand(
            RuntimeCommandId.New(),
            step.Id,
            new RuntimeCapabilityId("test.external"),
            "ExecuteProgram",
            BaseTimeUtc.AddSeconds(2),
            TimeSpan.FromSeconds(30));
        Assert.True(session.AcceptCommand(command.Id, BaseTimeUtc.AddSeconds(2)).Succeeded);
        Assert.True(session.StartCommand(command.Id, BaseTimeUtc.AddSeconds(3)).Succeeded);
        return command;
    }

    private sealed class RecordingProductionMaterialRepository(
        IProductionMaterialRepository inner) : IProductionMaterialRepository
    {
        public int TimelineQueryCount { get; private set; }

        public ProductionMaterialTimelineQuery? LastTimelineQuery { get; private set; }

        public void ResetTimelineQueries()
        {
            TimelineQueryCount = 0;
            LastTimelineQuery = null;
        }

        public ValueTask<bool> TryAddAsync(
            ProductionUnit productionUnit,
            CancellationToken cancellationToken = default) =>
            inner.TryAddAsync(productionUnit, cancellationToken);

        public ValueTask<bool> TryAddAsync(
            ProductionLot productionLot,
            CancellationToken cancellationToken = default) =>
            inner.TryAddAsync(productionLot, cancellationToken);

        public ValueTask<bool> TryAddAsync(
            Carrier carrier,
            CancellationToken cancellationToken = default) =>
            inner.TryAddAsync(carrier, cancellationToken);

        public ValueTask<bool> TryAddAsync(
            SlotOccupancy slot,
            CancellationToken cancellationToken = default) =>
            inner.TryAddAsync(slot, cancellationToken);

        public ValueTask<bool> TryAddAsync(
            MaterialGenealogyLink link,
            CancellationToken cancellationToken = default) =>
            inner.TryAddAsync(link, cancellationToken);

        public ValueTask<ProductionMaterialPersistenceEntry<ProductionUnit>?>
            GetProductionUnitAsync(
                ProductionUnitId productionUnitId,
                CancellationToken cancellationToken = default) =>
            inner.GetProductionUnitAsync(productionUnitId, cancellationToken);

        public ValueTask<ProductionMaterialPersistenceEntry<ProductionLot>?> GetProductionLotAsync(
            ProductionLotId productionLotId,
            CancellationToken cancellationToken = default) =>
            inner.GetProductionLotAsync(productionLotId, cancellationToken);

        public ValueTask<ProductionMaterialPersistenceEntry<Carrier>?> GetCarrierAsync(
            CarrierId carrierId,
            CancellationToken cancellationToken = default) =>
            inner.GetCarrierAsync(carrierId, cancellationToken);

        public ValueTask<ProductionMaterialPersistenceEntry<SlotOccupancy>?> GetSlotAsync(
            SlotAddress slot,
            CancellationToken cancellationToken = default) =>
            inner.GetSlotAsync(slot, cancellationToken);

        public ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<ProductionUnit>>>
            ListProductionUnitsAsync(CancellationToken cancellationToken = default) =>
            inner.ListProductionUnitsAsync(cancellationToken);

        public ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<Carrier>>>
            ListCarriersAsync(CancellationToken cancellationToken = default) =>
            inner.ListCarriersAsync(cancellationToken);

        public ValueTask<IReadOnlyCollection<ProductionMaterialPersistenceEntry<SlotOccupancy>>>
            ListSlotsAsync(
                string? lineId = null,
                string? stationSystemId = null,
                CancellationToken cancellationToken = default) =>
            inner.ListSlotsAsync(lineId, stationSystemId, cancellationToken);

        public ValueTask<IReadOnlyCollection<MaterialGenealogyLink>> ListGenealogyLinksAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListGenealogyLinksAsync(cancellationToken);

        public async ValueTask<IReadOnlyCollection<ProductionMaterialTimelineEntry>>
            ListTimelineAsync(
                ProductionMaterialTimelineQuery query,
                CancellationToken cancellationToken = default)
        {
            TimelineQueryCount++;
            LastTimelineQuery = query;
            var timeline = await inner.ListTimelineAsync(query, cancellationToken);
            return query.Mode == ProductionMaterialTimelineQueryMode.UnionScope
                && timeline.FirstOrDefault() is { } duplicate
                ? [.. timeline, duplicate]
                : timeline;
        }

        public ValueTask CommitAsync(
            ProductionMaterialCommit commit,
            CancellationToken cancellationToken = default) =>
            inner.CommitAsync(commit, cancellationToken);
    }
}
