using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.Records;

internal static class TraceRecordMapper
{
    public static TraceRecordDetails ToDetails(TraceRecord traceRecord)
    {
        return new TraceRecordDetails(
            traceRecord.Id.Value,
            traceRecord.RuntimeSessionId.Value,
            traceRecord.SerialNumber,
            traceRecord.BatchId,
            traceRecord.StationId.Value,
            traceRecord.FixtureId,
            traceRecord.ProcessDefinitionId.Value,
            traceRecord.ProcessVersionId.Value,
            traceRecord.ConfigurationSnapshotId.Value,
            traceRecord.RecipeSnapshotId.Value,
            traceRecord.DeviceId.Value,
            traceRecord.Judgement.ToString(),
            traceRecord.StartedAtUtc,
            traceRecord.CompletedAtUtc,
            traceRecord.RecordedBy.Value,
            traceRecord.Measurements.Select(ToDetails).ToArray(),
            traceRecord.Artifacts.Select(ToDetails).ToArray(),
            traceRecord.AuditEntries.Select(ToDetails).ToArray());
    }

    public static TraceRecordSummary ToSummary(TraceRecord traceRecord)
    {
        return new TraceRecordSummary(
            traceRecord.Id.Value,
            traceRecord.RuntimeSessionId.Value,
            traceRecord.SerialNumber,
            traceRecord.BatchId,
            traceRecord.StationId.Value,
            traceRecord.FixtureId,
            traceRecord.ProcessVersionId.Value,
            traceRecord.ConfigurationSnapshotId.Value,
            traceRecord.RecipeSnapshotId.Value,
            traceRecord.DeviceId.Value,
            traceRecord.Judgement.ToString(),
            traceRecord.CompletedAtUtc);
    }

    private static MeasurementRecordDetails ToDetails(MeasurementRecord measurement)
    {
        return new MeasurementRecordDetails(
            measurement.Id.Value,
            measurement.Name,
            measurement.NumericValue,
            measurement.TextValue,
            measurement.Unit,
            measurement.DeviceId.Value,
            measurement.RuntimeCommandId?.Value,
            measurement.Passed,
            measurement.MeasuredAtUtc);
    }

    private static ArtifactRecordDetails ToDetails(ArtifactRecord artifact)
    {
        return new ArtifactRecordDetails(
            artifact.Id.Value,
            artifact.Name,
            artifact.Kind.ToString(),
            artifact.StorageKey,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256,
            artifact.DeviceId.Value,
            artifact.CapturedAtUtc);
    }

    private static AuditEntryDetails ToDetails(AuditEntry auditEntry)
    {
        return new AuditEntryDetails(
            auditEntry.Id.Value,
            auditEntry.ActorId.Value,
            auditEntry.Action,
            auditEntry.Detail,
            auditEntry.OccurredAtUtc);
    }
}
