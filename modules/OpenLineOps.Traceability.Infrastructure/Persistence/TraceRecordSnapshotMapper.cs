using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Infrastructure.Persistence;

internal static class TraceRecordSnapshotMapper
{
    public static PersistedTraceRecord ToSnapshot(TraceRecord traceRecord)
    {
        return new PersistedTraceRecord(
            traceRecord.Id.Value,
            traceRecord.RuntimeSessionId.Value,
            traceRecord.ProjectId,
            traceRecord.ApplicationId,
            traceRecord.ProjectSnapshotId,
            traceRecord.TopologyId,
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
            traceRecord.Measurements.Select(ToSnapshot).ToArray(),
            traceRecord.Artifacts.Select(ToSnapshot).ToArray(),
            traceRecord.AuditEntries.Select(ToSnapshot).ToArray());
    }

    public static TraceRecord ToAggregate(PersistedTraceRecord snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return TraceRecord.Restore(
            new TraceRecordId(snapshot.TraceRecordId),
            new RuntimeSessionId(snapshot.RuntimeSessionId),
            snapshot.SerialNumber,
            snapshot.BatchId,
            new StationId(snapshot.StationId),
            snapshot.FixtureId,
            new ProcessDefinitionId(snapshot.ProcessDefinitionId),
            new ProcessVersionId(snapshot.ProcessVersionId),
            new ConfigurationSnapshotId(snapshot.ConfigurationSnapshotId),
            new RecipeSnapshotId(snapshot.RecipeSnapshotId),
            new DeviceId(snapshot.DeviceId),
            ParseEnum<ResultJudgement>(snapshot.Judgement, nameof(snapshot.Judgement)),
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc,
            new ActorId(snapshot.RecordedBy),
            snapshot.Measurements.Select(ToAggregate),
            snapshot.Artifacts.Select(ToAggregate),
            snapshot.AuditEntries.Select(ToAggregate),
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.ProjectSnapshotId,
            snapshot.TopologyId);
    }

    private static PersistedMeasurementRecord ToSnapshot(MeasurementRecord measurement)
    {
        return new PersistedMeasurementRecord(
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

    private static PersistedArtifactRecord ToSnapshot(ArtifactRecord artifact)
    {
        return new PersistedArtifactRecord(
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

    private static PersistedAuditEntry ToSnapshot(AuditEntry auditEntry)
    {
        return new PersistedAuditEntry(
            auditEntry.Id.Value,
            auditEntry.ActorId.Value,
            auditEntry.Action,
            auditEntry.Detail,
            auditEntry.OccurredAtUtc);
    }

    private static MeasurementRecord ToAggregate(PersistedMeasurementRecord measurement)
    {
        return new MeasurementRecord(
            new MeasurementRecordId(measurement.MeasurementRecordId),
            measurement.Name,
            measurement.NumericValue,
            measurement.TextValue,
            measurement.Unit,
            new DeviceId(measurement.DeviceId),
            measurement.RuntimeCommandId is null
                ? null
                : new RuntimeCommandId(measurement.RuntimeCommandId.Value),
            measurement.Passed,
            measurement.MeasuredAtUtc);
    }

    private static ArtifactRecord ToAggregate(PersistedArtifactRecord artifact)
    {
        return new ArtifactRecord(
            new ArtifactRecordId(artifact.ArtifactRecordId),
            artifact.Name,
            ParseEnum<ArtifactKind>(artifact.Kind, nameof(artifact.Kind)),
            artifact.StorageKey,
            artifact.MediaType,
            artifact.SizeBytes,
            artifact.Sha256,
            new DeviceId(artifact.DeviceId),
            artifact.CapturedAtUtc);
    }

    private static AuditEntry ToAggregate(PersistedAuditEntry auditEntry)
    {
        return new AuditEntry(
            new AuditEntryId(auditEntry.AuditEntryId),
            new ActorId(auditEntry.ActorId),
            auditEntry.Action,
            auditEntry.Detail,
            auditEntry.OccurredAtUtc);
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Persisted {fieldName} value '{value}' is invalid.");
    }
}

internal sealed record PersistedTraceRecord(
    Guid TraceRecordId,
    Guid RuntimeSessionId,
    string? ProjectId,
    string? ApplicationId,
    string? ProjectSnapshotId,
    string? TopologyId,
    string SerialNumber,
    string? BatchId,
    string StationId,
    string? FixtureId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string DeviceId,
    string Judgement,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string RecordedBy,
    PersistedMeasurementRecord[] Measurements,
    PersistedArtifactRecord[] Artifacts,
    PersistedAuditEntry[] AuditEntries);

internal sealed record PersistedMeasurementRecord(
    Guid MeasurementRecordId,
    string Name,
    decimal? NumericValue,
    string? TextValue,
    string? Unit,
    string DeviceId,
    Guid? RuntimeCommandId,
    bool? Passed,
    DateTimeOffset MeasuredAtUtc);

internal sealed record PersistedArtifactRecord(
    Guid ArtifactRecordId,
    string Name,
    string Kind,
    string StorageKey,
    string? MediaType,
    long SizeBytes,
    string? Sha256,
    string DeviceId,
    DateTimeOffset CapturedAtUtc);

internal sealed record PersistedAuditEntry(
    Guid AuditEntryId,
    string ActorId,
    string Action,
    string? Detail,
    DateTimeOffset OccurredAtUtc);
