using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Tests;

internal static class TraceTestData
{
    public static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    public static TraceRecord CreateTrace(
        string runId,
        string productionUnitIdentityValue,
        DateTimeOffset completedAtUtc,
        string stationSystemId = "station-a",
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
        var command = new TraceCommandRecord(
            new RuntimeCommandId(runtimeCommandId),
            Guid.NewGuid(),
            "action.inspect.width",
            TraceTargetKind.System,
            stationSystemId,
            "capability.inspect",
            "Inspect",
            TraceCommandStatus.Completed,
            judgement,
            BaseTimeUtc.AddSeconds(10),
            BaseTimeUtc.AddSeconds(40),
            BaseTimeUtc.AddSeconds(11),
            BaseTimeUtc.AddSeconds(12),
            BaseTimeUtc.AddSeconds(20),
            judgement == ResultJudgement.Failed ? "Inspection failed." : "ok",
            null);
        var measurement = new MeasurementRecord(
            new MeasurementRecordId(runtimeCommandId),
            "Inspect",
            null,
            judgement == ResultJudgement.Failed ? "Inspection failed." : "ok",
            null,
            new DeviceId(deviceId),
            new RuntimeCommandId(runtimeCommandId),
            command.ActionId,
            TraceTargetKind.System,
            stationSystemId,
            command.Status,
            judgement switch
            {
                ResultJudgement.Passed => true,
                ResultJudgement.Failed => false,
                _ => null
            },
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
        var operation = new TraceOperationExecution(
            "operation.inspect@0001",
            "operation.inspect",
            1,
            stationSystemId,
            new StationId(stationSystemId),
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId(processVersionId),
            new ConfigurationSnapshotId("config-snapshot-a"),
            new RecipeSnapshotId("recipe-snapshot-a"),
            new RuntimeSessionId(runtimeSessionId),
            TraceRuntimeSessionStatus.Completed,
            ExecutionStatus.Completed,
            judgement,
            BaseTimeUtc,
            completedAtUtc,
            null,
            null,
            1,
            1,
            0,
            [command],
            [measurement],
            artifacts,
            [],
            [new TraceOperationOutput("inspection.result", "Text", "\"recorded\"")],
            [
                new TraceResourceFencingToken("Station", stationSystemId, 1),
                new TraceResourceFencingToken("Device", deviceId, 7)
            ]);

        return TraceRecord.Create(
            new TraceRecordId(productionRunId),
            new ProductionRunId(productionRunId),
            "project-trace-a",
            "application-trace-a",
            projectSnapshotId,
            "topology-trace-a",
            productionLineDefinitionId,
            "product-model-a",
            "serialNumber",
            productionUnitIdentityValue,
            "lot-a",
            "carrier-a",
            new ActorId("operator-a"),
            ExecutionStatus.Completed,
            judgement,
            judgement == ResultJudgement.Failed
                ? ProductDisposition.Nonconforming
                : ProductDisposition.Completed,
            BaseTimeUtc,
            BaseTimeUtc,
            completedAtUtc,
            null,
            null,
            [operation],
            [],
            [
                new AuditEntry(
                    AuditEntryId.New(),
                    new ActorId("operator-a"),
                    "ProductionRun.Completed",
                    "Production Run trace recorded.",
                    completedAtUtc)
            ]);
    }
}
