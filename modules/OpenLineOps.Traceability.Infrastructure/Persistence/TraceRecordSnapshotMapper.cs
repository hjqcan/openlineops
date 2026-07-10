using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Infrastructure.Persistence;

internal static class TraceRecordSnapshotMapper
{
    public static PersistedTraceRecord ToSnapshot(TraceRecord record)
    {
        return new PersistedTraceRecord(
            record.Id.Value,
            record.ProductionRunId.Value,
            record.ProjectId,
            record.ApplicationId,
            record.ProjectSnapshotId,
            record.TopologyId,
            record.ProductionLineDefinitionId,
            record.DutModelId,
            record.DutIdentityInputKey,
            record.DutIdentityValue,
            record.BatchId,
            record.FixtureId,
            record.DeviceId,
            record.ActorId.Value,
            record.RunStatus.ToString(),
            record.Judgement.ToString(),
            record.CreatedAtUtc,
            record.StartedAtUtc,
            record.CompletedAtUtc,
            record.FailureCode,
            record.FailureReason,
            record.Stages.Select(ToSnapshot).ToArray(),
            record.AuditEntries.Select(ToSnapshot).ToArray());
    }

    public static TraceRecord ToAggregate(PersistedTraceRecord snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return TraceRecord.Restore(
            new TraceRecordId(snapshot.TraceRecordId),
            new ProductionRunId(snapshot.ProductionRunId),
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.ProjectSnapshotId,
            snapshot.TopologyId,
            snapshot.ProductionLineDefinitionId,
            snapshot.DutModelId,
            snapshot.DutIdentityInputKey,
            snapshot.DutIdentityValue,
            snapshot.BatchId,
            snapshot.FixtureId,
            snapshot.DeviceId,
            new ActorId(snapshot.ActorId),
            ParseEnum<TraceProductionRunStatus>(snapshot.RunStatus, nameof(snapshot.RunStatus)),
            ParseEnum<ResultJudgement>(snapshot.Judgement, nameof(snapshot.Judgement)),
            snapshot.CreatedAtUtc,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc,
            snapshot.FailureCode,
            snapshot.FailureReason,
            snapshot.Stages.Select(ToAggregate),
            snapshot.AuditEntries.Select(ToAggregate));
    }

    private static PersistedTraceStageExecution ToSnapshot(TraceStageExecution stage)
    {
        return new PersistedTraceStageExecution(
            stage.StageId,
            stage.Sequence,
            stage.WorkstationId,
            stage.StationId.Value,
            stage.ProcessDefinitionId.Value,
            stage.ProcessVersionId.Value,
            stage.ConfigurationSnapshotId.Value,
            stage.RecipeSnapshotId.Value,
            stage.RuntimeSessionId?.Value,
            stage.RuntimeSessionStatus?.ToString(),
            stage.Status.ToString(),
            stage.StartedAtUtc,
            stage.CompletedAtUtc,
            stage.FailureCode,
            stage.FailureReason,
            stage.CompletedStepCount,
            stage.CommandCount,
            stage.IncidentCount,
            stage.Commands.Select(ToSnapshot).ToArray(),
            stage.Measurements.Select(ToSnapshot).ToArray(),
            stage.Artifacts.Select(ToSnapshot).ToArray(),
            stage.Incidents.Select(ToSnapshot).ToArray());
    }

    private static TraceStageExecution ToAggregate(PersistedTraceStageExecution stage)
    {
        return new TraceStageExecution(
            stage.StageId,
            stage.Sequence,
            stage.WorkstationId,
            new StationId(stage.StationId),
            new ProcessDefinitionId(stage.ProcessDefinitionId),
            new ProcessVersionId(stage.ProcessVersionId),
            new ConfigurationSnapshotId(stage.ConfigurationSnapshotId),
            new RecipeSnapshotId(stage.RecipeSnapshotId),
            stage.RuntimeSessionId is null ? null : new RuntimeSessionId(stage.RuntimeSessionId.Value),
            stage.RuntimeSessionStatus is null
                ? null
                : ParseEnum<TraceRuntimeSessionStatus>(
                    stage.RuntimeSessionStatus,
                    nameof(stage.RuntimeSessionStatus)),
            ParseEnum<TraceStageStatus>(stage.Status, nameof(stage.Status)),
            stage.StartedAtUtc,
            stage.CompletedAtUtc,
            stage.FailureCode,
            stage.FailureReason,
            stage.CompletedStepCount,
            stage.CommandCount,
            stage.IncidentCount,
            stage.Commands.Select(ToAggregate),
            stage.Measurements.Select(ToAggregate),
            stage.Artifacts.Select(ToAggregate),
            stage.Incidents.Select(ToAggregate));
    }

    private static PersistedTraceCommand ToSnapshot(TraceCommandRecord command)
    {
        return new PersistedTraceCommand(
            command.RuntimeCommandId.Value,
            command.RuntimeStepId,
            command.ActionId,
            command.TargetKind.ToString(),
            command.TargetId,
            command.TargetCapabilityId,
            command.CommandName,
            command.Status.ToString(),
            command.SemanticOutcome?.ToString(),
            command.CreatedAtUtc,
            command.DeadlineAtUtc,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason);
    }

    private static TraceCommandRecord ToAggregate(PersistedTraceCommand command)
    {
        return new TraceCommandRecord(
            new RuntimeCommandId(command.RuntimeCommandId),
            command.RuntimeStepId,
            command.ActionId,
            ParseEnum<TraceTargetKind>(command.TargetKind, nameof(command.TargetKind)),
            command.TargetId,
            command.TargetCapabilityId,
            command.CommandName,
            ParseEnum<TraceCommandStatus>(command.Status, nameof(command.Status)),
            command.SemanticOutcome is null
                ? null
                : ParseEnum<TraceCommandSemanticOutcome>(
                    command.SemanticOutcome,
                    nameof(command.SemanticOutcome)),
            command.CreatedAtUtc,
            command.DeadlineAtUtc,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason);
    }

    private static PersistedMeasurementRecord ToSnapshot(MeasurementRecord measurement)
    {
        return new PersistedMeasurementRecord(
            measurement.Id.Value,
            measurement.Name,
            measurement.NumericValue,
            measurement.TextValue,
            measurement.Unit,
            measurement.DeviceId?.Value,
            measurement.RuntimeCommandId?.Value,
            measurement.ActionId,
            measurement.TargetKind.ToString(),
            measurement.TargetId,
            measurement.CommandStatus.ToString(),
            measurement.Passed,
            measurement.MeasuredAtUtc);
    }

    private static MeasurementRecord ToAggregate(PersistedMeasurementRecord measurement)
    {
        return new MeasurementRecord(
            new MeasurementRecordId(measurement.MeasurementRecordId),
            measurement.Name,
            measurement.NumericValue,
            measurement.TextValue,
            measurement.Unit,
            measurement.DeviceId is null ? null : new DeviceId(measurement.DeviceId),
            measurement.RuntimeCommandId is null ? null : new RuntimeCommandId(measurement.RuntimeCommandId.Value),
            measurement.ActionId,
            ParseEnum<TraceTargetKind>(measurement.TargetKind, nameof(measurement.TargetKind)),
            measurement.TargetId,
            ParseEnum<TraceCommandStatus>(measurement.CommandStatus, nameof(measurement.CommandStatus)),
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
            artifact.DeviceId?.Value,
            artifact.CapturedAtUtc);
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
            artifact.DeviceId is null ? null : new DeviceId(artifact.DeviceId),
            artifact.CapturedAtUtc);
    }

    private static PersistedTraceIncident ToSnapshot(TraceIncidentRecord incident)
    {
        return new PersistedTraceIncident(
            incident.RuntimeIncidentId,
            incident.Severity.ToString(),
            incident.Code,
            incident.Message,
            incident.OccurredAtUtc);
    }

    private static TraceIncidentRecord ToAggregate(PersistedTraceIncident incident)
    {
        return new TraceIncidentRecord(
            incident.RuntimeIncidentId,
            ParseEnum<TraceIncidentSeverity>(incident.Severity, nameof(incident.Severity)),
            incident.Code,
            incident.Message,
            incident.OccurredAtUtc);
    }

    private static PersistedAuditEntry ToSnapshot(AuditEntry entry)
    {
        return new PersistedAuditEntry(
            entry.Id.Value,
            entry.ActorId.Value,
            entry.Action,
            entry.Detail,
            entry.OccurredAtUtc);
    }

    private static AuditEntry ToAggregate(PersistedAuditEntry entry)
    {
        return new AuditEntry(
            new AuditEntryId(entry.AuditEntryId),
            new ActorId(entry.ActorId),
            entry.Action,
            entry.Detail,
            entry.OccurredAtUtc);
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum
    {
        if (CanonicalEnumToken.TryParse<TEnum>(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Persisted {fieldName} value '{value}' is invalid. Expected an exact, case-sensitive token: "
            + CanonicalEnumToken.ExpectedTokens<TEnum>());
    }
}

internal sealed record PersistedTraceRecord(
    Guid TraceRecordId,
    Guid ProductionRunId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    string DutModelId,
    string DutIdentityInputKey,
    string DutIdentityValue,
    string? BatchId,
    string? FixtureId,
    string? DeviceId,
    string ActorId,
    string RunStatus,
    string Judgement,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    PersistedTraceStageExecution[] Stages,
    PersistedAuditEntry[] AuditEntries);

internal sealed record PersistedTraceStageExecution(
    string StageId,
    int Sequence,
    string WorkstationId,
    string StationId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    Guid? RuntimeSessionId,
    string? RuntimeSessionStatus,
    string Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    PersistedTraceCommand[] Commands,
    PersistedMeasurementRecord[] Measurements,
    PersistedArtifactRecord[] Artifacts,
    PersistedTraceIncident[] Incidents);

internal sealed record PersistedTraceCommand(
    Guid RuntimeCommandId,
    Guid RuntimeStepId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string TargetCapabilityId,
    string CommandName,
    string Status,
    string? SemanticOutcome,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadlineAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultPayload,
    string? FailureReason);

internal sealed record PersistedMeasurementRecord(
    Guid MeasurementRecordId,
    string Name,
    decimal? NumericValue,
    string? TextValue,
    string? Unit,
    string? DeviceId,
    Guid? RuntimeCommandId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string CommandStatus,
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
    string? DeviceId,
    DateTimeOffset CapturedAtUtc);

internal sealed record PersistedTraceIncident(
    Guid RuntimeIncidentId,
    string Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc);

internal sealed record PersistedAuditEntry(
    Guid AuditEntryId,
    string ActorId,
    string Action,
    string? Detail,
    DateTimeOffset OccurredAtUtc);
