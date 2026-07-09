namespace OpenLineOps.Traceability.Application.Records;

public sealed record TraceRecordDetails(
    Guid TraceRecordId,
    Guid RuntimeSessionId,
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
    IReadOnlyCollection<MeasurementRecordDetails> Measurements,
    IReadOnlyCollection<ArtifactRecordDetails> Artifacts,
    IReadOnlyCollection<AuditEntryDetails> AuditEntries);

public sealed record TraceRecordSummary(
    Guid TraceRecordId,
    Guid RuntimeSessionId,
    string SerialNumber,
    string? BatchId,
    string StationId,
    string? FixtureId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string DeviceId,
    string Judgement,
    DateTimeOffset CompletedAtUtc);

public sealed record MeasurementRecordDetails(
    Guid MeasurementRecordId,
    string Name,
    decimal? NumericValue,
    string? TextValue,
    string? Unit,
    string DeviceId,
    Guid? RuntimeCommandId,
    bool? Passed,
    DateTimeOffset MeasuredAtUtc);

public sealed record ArtifactRecordDetails(
    Guid ArtifactRecordId,
    string Name,
    string Kind,
    string StorageKey,
    string? MediaType,
    long SizeBytes,
    string? Sha256,
    string DeviceId,
    DateTimeOffset CapturedAtUtc);

public sealed record AuditEntryDetails(
    Guid AuditEntryId,
    string ActorId,
    string Action,
    string? Detail,
    DateTimeOffset OccurredAtUtc);
