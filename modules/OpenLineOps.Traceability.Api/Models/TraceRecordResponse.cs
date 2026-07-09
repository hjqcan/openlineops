namespace OpenLineOps.Traceability.Api.Models;

public sealed record TraceRecordResponse(
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
    IReadOnlyCollection<MeasurementRecordResponse> Measurements,
    IReadOnlyCollection<ArtifactRecordResponse> Artifacts,
    IReadOnlyCollection<AuditEntryResponse> AuditEntries);

public sealed record TraceRecordSummaryResponse(
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

public sealed record MeasurementRecordResponse(
    Guid MeasurementRecordId,
    string Name,
    decimal? NumericValue,
    string? TextValue,
    string? Unit,
    string DeviceId,
    Guid? RuntimeCommandId,
    bool? Passed,
    DateTimeOffset MeasuredAtUtc);

public sealed record ArtifactRecordResponse(
    Guid ArtifactRecordId,
    string Name,
    string Kind,
    string StorageKey,
    string? MediaType,
    long SizeBytes,
    string? Sha256,
    string DeviceId,
    DateTimeOffset CapturedAtUtc);

public sealed record AuditEntryResponse(
    Guid AuditEntryId,
    string ActorId,
    string Action,
    string? Detail,
    DateTimeOffset OccurredAtUtc);
