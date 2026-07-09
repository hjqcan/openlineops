namespace OpenLineOps.Traceability.Application.Records;

public sealed record CreateTraceRecordRequest(
    Guid? TraceRecordId,
    Guid RuntimeSessionId,
    string? SerialNumber,
    string? BatchId,
    string? StationId,
    string? FixtureId,
    string? ProcessDefinitionId,
    string? ProcessVersionId,
    string? ConfigurationSnapshotId,
    string? RecipeSnapshotId,
    string? DeviceId,
    string? Judgement,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? RecordedBy,
    IReadOnlyCollection<CreateMeasurementRecordRequest>? Measurements,
    IReadOnlyCollection<CreateArtifactRecordRequest>? Artifacts,
    IReadOnlyCollection<CreateAuditEntryRequest>? AuditEntries,
    string? ProjectId = null,
    string? ApplicationId = null,
    string? ProjectSnapshotId = null,
    string? TopologyId = null);

public sealed record CreateMeasurementRecordRequest(
    Guid? MeasurementRecordId,
    string? Name,
    decimal? NumericValue,
    string? TextValue,
    string? Unit,
    string? DeviceId,
    Guid? RuntimeCommandId,
    bool? Passed,
    DateTimeOffset MeasuredAtUtc);

public sealed record CreateArtifactRecordRequest(
    Guid? ArtifactRecordId,
    string? Name,
    string? Kind,
    string? StorageKey,
    string? MediaType,
    long SizeBytes,
    string? Sha256,
    string? DeviceId,
    DateTimeOffset CapturedAtUtc);

public sealed record CreateAuditEntryRequest(
    Guid? AuditEntryId,
    string? ActorId,
    string? Action,
    string? Detail,
    DateTimeOffset OccurredAtUtc);
