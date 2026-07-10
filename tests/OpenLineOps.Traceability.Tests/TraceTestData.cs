using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Tests;

internal static class TraceTestData
{
    public static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    public static TraceRecord CreateTrace(
        string runId,
        string dutIdentityValue,
        DateTimeOffset completedAtUtc,
        string stationId = "station-a",
        string processVersionId = "process-packaging@2026.06.29",
        string deviceId = "vision-camera-a",
        ResultJudgement judgement = ResultJudgement.Passed,
        string projectSnapshotId = "project-snapshot-trace-a",
        string productionLineDefinitionId = "line-a",
        bool includeArtifact = true)
    {
        var productionRunId = Guid.Parse(runId);
        var runtimeSessionId = Guid.NewGuid();
        var runtimeCommandId = Guid.NewGuid();
        var failed = judgement == ResultJudgement.Failed;
        var command = new TraceCommandRecord(
            new RuntimeCommandId(runtimeCommandId),
            Guid.NewGuid(),
            "action.inspect.width",
            TraceTargetKind.System,
            stationId,
            "capability.inspect",
            "Inspect",
            failed ? TraceCommandStatus.Failed : TraceCommandStatus.Completed,
            failed ? TraceCommandSemanticOutcome.Failed : TraceCommandSemanticOutcome.Passed,
            BaseTimeUtc.AddSeconds(10),
            BaseTimeUtc.AddSeconds(40),
            BaseTimeUtc.AddSeconds(11),
            BaseTimeUtc.AddSeconds(12),
            BaseTimeUtc.AddSeconds(20),
            failed ? null : "ok",
            failed ? "Inspection failed." : null);
        var measurement = new MeasurementRecord(
            new MeasurementRecordId(runtimeCommandId),
            "Inspect",
            null,
            failed ? "Inspection failed." : "ok",
            null,
            new DeviceId(deviceId),
            new RuntimeCommandId(runtimeCommandId),
            command.ActionId,
            TraceTargetKind.System,
            stationId,
            command.Status,
            !failed,
            command.CompletedAtUtc!.Value);
        var artifacts = includeArtifact
            ? new[]
            {
                new ArtifactRecord(
                    ArtifactRecordId.New(),
                    "inspection-log",
                    ArtifactKind.Log,
                    $"trace/{productionRunId:D}/inspection.log",
                    "text/plain",
                    128,
                    null,
                    new DeviceId(deviceId),
                    completedAtUtc)
            }
            : [];
        var stage = new TraceStageExecution(
            "stage-inspect",
            1,
            "workstation-a",
            new StationId(stationId),
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId(processVersionId),
            new ConfigurationSnapshotId("config-snapshot-a"),
            new RecipeSnapshotId("recipe-snapshot-a"),
            new RuntimeSessionId(runtimeSessionId),
            failed ? TraceRuntimeSessionStatus.Failed : TraceRuntimeSessionStatus.Completed,
            failed ? TraceStageStatus.Failed : TraceStageStatus.Completed,
            BaseTimeUtc,
            completedAtUtc,
            failed ? "Runtime.CommandFailed" : null,
            failed ? "Inspection failed." : null,
            failed ? 0 : 1,
            1,
            0,
            [command],
            [measurement],
            artifacts,
            []);

        return TraceRecord.Create(
            new TraceRecordId(productionRunId),
            new ProductionRunId(productionRunId),
            "project-trace-a",
            "application-trace-a",
            projectSnapshotId,
            "topology-trace-a",
            productionLineDefinitionId,
            "dut-model-a",
            "serialNumber",
            dutIdentityValue,
            "batch-a",
            "fixture-a",
            deviceId,
            new ActorId("operator-a"),
            failed ? TraceProductionRunStatus.Failed : TraceProductionRunStatus.Completed,
            judgement,
            BaseTimeUtc,
            BaseTimeUtc,
            completedAtUtc,
            failed ? "Runtime.StageFailed" : null,
            failed ? "Inspection failed." : null,
            [stage],
            [
                new AuditEntry(
                    AuditEntryId.New(),
                    new ActorId("operator-a"),
                    failed ? "ProductionRun.Failed" : "ProductionRun.Completed",
                    "Production run trace recorded.",
                    completedAtUtc)
            ]);
    }
}
