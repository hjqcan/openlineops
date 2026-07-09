using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Traceability.Domain.Events;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Operations;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed class TraceRecord : AggregateRoot<TraceRecordId>
{
    private readonly List<MeasurementRecord> _measurements = [];
    private readonly List<ArtifactRecord> _artifacts = [];
    private readonly List<AuditEntry> _auditEntries = [];

    private TraceRecord(
        TraceRecordId id,
        RuntimeSessionId runtimeSessionId,
        string serialNumber,
        string? batchId,
        StationId stationId,
        string? fixtureId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        DeviceId deviceId,
        ResultJudgement judgement,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        ActorId recordedBy)
        : base(id)
    {
        if (completedAtUtc < startedAtUtc)
        {
            throw new ArgumentException("Completed time cannot be earlier than started time.", nameof(completedAtUtc));
        }

        RuntimeSessionId = runtimeSessionId;
        SerialNumber = TraceabilityIdGuard.NotBlank(serialNumber, nameof(serialNumber));
        BatchId = TraceabilityIdGuard.OptionalText(batchId);
        StationId = stationId;
        FixtureId = TraceabilityIdGuard.OptionalText(fixtureId);
        ProcessDefinitionId = processDefinitionId;
        ProcessVersionId = processVersionId;
        ConfigurationSnapshotId = configurationSnapshotId;
        RecipeSnapshotId = recipeSnapshotId;
        DeviceId = deviceId;
        Judgement = judgement;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        RecordedBy = recordedBy;
    }

    public RuntimeSessionId RuntimeSessionId { get; }

    public string SerialNumber { get; }

    public string? BatchId { get; }

    public StationId StationId { get; }

    public string? FixtureId { get; }

    public ProcessDefinitionId ProcessDefinitionId { get; }

    public ProcessVersionId ProcessVersionId { get; }

    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }

    public RecipeSnapshotId RecipeSnapshotId { get; }

    public DeviceId DeviceId { get; }

    public ResultJudgement Judgement { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public ActorId RecordedBy { get; }

    public IReadOnlyCollection<MeasurementRecord> Measurements => _measurements.AsReadOnly();

    public IReadOnlyCollection<ArtifactRecord> Artifacts => _artifacts.AsReadOnly();

    public IReadOnlyCollection<AuditEntry> AuditEntries => _auditEntries.AsReadOnly();

    public static TraceRecord CreateCompleted(
        TraceRecordId id,
        RuntimeSessionId runtimeSessionId,
        string serialNumber,
        string? batchId,
        StationId stationId,
        string? fixtureId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        DeviceId deviceId,
        ResultJudgement judgement,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        ActorId recordedBy)
    {
        var record = new TraceRecord(
            id,
            runtimeSessionId,
            serialNumber,
            batchId,
            stationId,
            fixtureId,
            processDefinitionId,
            processVersionId,
            configurationSnapshotId,
            recipeSnapshotId,
            deviceId,
            judgement,
            startedAtUtc,
            completedAtUtc,
            recordedBy);

        record.RaiseDomainEvent(new TraceRecordCreatedDomainEvent(id, runtimeSessionId, completedAtUtc));

        return record;
    }

    public static TraceRecord Restore(
        TraceRecordId id,
        RuntimeSessionId runtimeSessionId,
        string serialNumber,
        string? batchId,
        StationId stationId,
        string? fixtureId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        DeviceId deviceId,
        ResultJudgement judgement,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        ActorId recordedBy,
        IEnumerable<MeasurementRecord> measurements,
        IEnumerable<ArtifactRecord> artifacts,
        IEnumerable<AuditEntry> auditEntries)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(auditEntries);

        var record = new TraceRecord(
            id,
            runtimeSessionId,
            serialNumber,
            batchId,
            stationId,
            fixtureId,
            processDefinitionId,
            processVersionId,
            configurationSnapshotId,
            recipeSnapshotId,
            deviceId,
            judgement,
            startedAtUtc,
            completedAtUtc,
            recordedBy);

        foreach (var measurement in measurements)
        {
            EnsureAccepted(record.AddMeasurement(measurement));
        }

        foreach (var artifact in artifacts)
        {
            EnsureAccepted(record.AttachArtifact(artifact));
        }

        foreach (var auditEntry in auditEntries)
        {
            EnsureAccepted(record.RecordAudit(auditEntry));
        }

        record.ClearDomainEvents();

        return record;
    }

    public TraceOperationResult AddMeasurement(MeasurementRecord measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        if (_measurements.Any(candidate => candidate.Id == measurement.Id))
        {
            return TraceOperationResult.Rejected(
                "Traceability.MeasurementAlreadyExists",
                $"Measurement {measurement.Id} already exists in trace record {Id}.");
        }

        _measurements.Add(measurement);

        return TraceOperationResult.Accepted("Measurement added.");
    }

    public TraceOperationResult AttachArtifact(ArtifactRecord artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (_artifacts.Any(candidate => candidate.Id == artifact.Id))
        {
            return TraceOperationResult.Rejected(
                "Traceability.ArtifactAlreadyExists",
                $"Artifact {artifact.Id} already exists in trace record {Id}.");
        }

        if (_artifacts.Any(candidate => string.Equals(
            candidate.StorageKey,
            artifact.StorageKey,
            StringComparison.OrdinalIgnoreCase)))
        {
            return TraceOperationResult.Rejected(
                "Traceability.ArtifactStorageKeyAlreadyExists",
                $"Artifact storage key {artifact.StorageKey} already exists in trace record {Id}.");
        }

        _artifacts.Add(artifact);

        return TraceOperationResult.Accepted("Artifact attached.");
    }

    public TraceOperationResult RecordAudit(AuditEntry auditEntry)
    {
        ArgumentNullException.ThrowIfNull(auditEntry);

        if (_auditEntries.Any(candidate => candidate.Id == auditEntry.Id))
        {
            return TraceOperationResult.Rejected(
                "Traceability.AuditEntryAlreadyExists",
                $"Audit entry {auditEntry.Id} already exists in trace record {Id}.");
        }

        _auditEntries.Add(auditEntry);

        return TraceOperationResult.Accepted("Audit entry recorded.");
    }

    private static void EnsureAccepted(TraceOperationResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Message);
        }
    }
}
