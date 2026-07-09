using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Tests;

public sealed class TraceRecordTests
{
    private static readonly DateTimeOffset StartedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAtUtc = StartedAtUtc.AddMinutes(2);

    [Fact]
    public void CreateCompletedLinksRuntimeConfigurationStationProcessRecipeDeviceAndActor()
    {
        var trace = CreateTrace("SMX-0001");

        Assert.Equal("SMX-0001", trace.SerialNumber);
        Assert.Equal("batch-a", trace.BatchId);
        Assert.Equal("fixture-a", trace.FixtureId);
        Assert.Equal("station-a", trace.StationId.Value);
        Assert.Equal("process-packaging", trace.ProcessDefinitionId.Value);
        Assert.Equal("process-packaging@2026.06.29", trace.ProcessVersionId.Value);
        Assert.Equal("config-snapshot-a", trace.ConfigurationSnapshotId.Value);
        Assert.Equal("recipe-snapshot-a", trace.RecipeSnapshotId.Value);
        Assert.Equal("vision-camera-a", trace.DeviceId.Value);
        Assert.Equal("operator-a", trace.RecordedBy.Value);
        Assert.Equal(ResultJudgement.Passed, trace.Judgement);
        Assert.Single(trace.DomainEvents);
    }

    [Fact]
    public void CreateCompletedRejectsMissingRequiredTraceabilityLink()
    {
        var exception = Assert.Throws<ArgumentException>(() => TraceRecord.CreateCompleted(
            TraceRecordId.New(),
            new RuntimeSessionId(Guid.NewGuid()),
            " ",
            "batch-a",
            new StationId("station-a"),
            "fixture-a",
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId("process-packaging@2026.06.29"),
            new ConfigurationSnapshotId("config-snapshot-a"),
            new RecipeSnapshotId("recipe-snapshot-a"),
            new DeviceId("vision-camera-a"),
            ResultJudgement.Passed,
            StartedAtUtc,
            CompletedAtUtc,
            new ActorId("operator-a")));

        Assert.Equal("serialNumber", exception.ParamName);
    }

    [Fact]
    public void AttachRecordsPersistsMeasurementArtifactMetadataAndAuditEntry()
    {
        var trace = CreateTrace("SMX-0002");
        var commandId = new RuntimeCommandId(Guid.NewGuid());
        var measurement = new MeasurementRecord(
            MeasurementRecordId.New(),
            "width",
            10.12m,
            null,
            "mm",
            new DeviceId("vision-camera-a"),
            commandId,
            true,
            CompletedAtUtc.AddSeconds(-10));
        var artifact = new ArtifactRecord(
            ArtifactRecordId.New(),
            "inspection-image",
            ArtifactKind.Image,
            "artifacts/SMX-0002/inspection.png",
            "image/png",
            2048,
            "sha256-demo",
            new DeviceId("vision-camera-a"),
            CompletedAtUtc.AddSeconds(-5));
        var auditEntry = new AuditEntry(
            AuditEntryId.New(),
            new ActorId("operator-a"),
            "TraceRecord.Completed",
            "Recorded by runtime completion handler.",
            CompletedAtUtc);

        Assert.True(trace.AddMeasurement(measurement).Succeeded);
        Assert.True(trace.AttachArtifact(artifact).Succeeded);
        Assert.True(trace.RecordAudit(auditEntry).Succeeded);

        var storedMeasurement = Assert.Single(trace.Measurements);
        Assert.Equal("width", storedMeasurement.Name);
        Assert.Equal(commandId, storedMeasurement.RuntimeCommandId);

        var storedArtifact = Assert.Single(trace.Artifacts);
        Assert.Equal("artifacts/SMX-0002/inspection.png", storedArtifact.StorageKey);
        Assert.Equal("image/png", storedArtifact.MediaType);
        Assert.Equal(2048, storedArtifact.SizeBytes);

        var storedAudit = Assert.Single(trace.AuditEntries);
        Assert.Equal("TraceRecord.Completed", storedAudit.Action);
    }

    internal static TraceRecord CreateTrace(string serialNumber, DateTimeOffset? completedAtUtc = null)
    {
        var completedAt = completedAtUtc ?? CompletedAtUtc;

        return TraceRecord.CreateCompleted(
            TraceRecordId.New(),
            new RuntimeSessionId(Guid.NewGuid()),
            serialNumber,
            "batch-a",
            new StationId("station-a"),
            "fixture-a",
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId("process-packaging@2026.06.29"),
            new ConfigurationSnapshotId("config-snapshot-a"),
            new RecipeSnapshotId("recipe-snapshot-a"),
            new DeviceId("vision-camera-a"),
            ResultJudgement.Passed,
            StartedAtUtc,
            completedAt,
            new ActorId("operator-a"));
    }
}
