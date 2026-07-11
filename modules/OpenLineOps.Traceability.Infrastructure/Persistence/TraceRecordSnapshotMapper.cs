using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Runtime.Contracts;
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
            record.ProductModelId,
            record.ProductionUnitIdentityInputKey,
            record.ProductionUnitIdentityValue,
            record.LotId,
            record.CarrierId,
            record.ActorId.Value,
            record.ExecutionStatus.ToString(),
            record.Judgement.ToString(),
            record.Disposition.ToString(),
            record.CreatedAtUtc,
            record.StartedAtUtc,
            record.CompletedAtUtc,
            record.FailureCode,
            record.FailureReason,
            record.Operations.Select(ToSnapshot).ToArray(),
            record.RouteDecisions.Select(ToSnapshot).ToArray(),
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
            snapshot.ProductModelId,
            snapshot.ProductionUnitIdentityInputKey,
            snapshot.ProductionUnitIdentityValue,
            snapshot.LotId,
            snapshot.CarrierId,
            new ActorId(snapshot.ActorId),
            ParseEnum<ExecutionStatus>(snapshot.ExecutionStatus, nameof(snapshot.ExecutionStatus)),
            ParseEnum<ResultJudgement>(snapshot.Judgement, nameof(snapshot.Judgement)),
            ParseEnum<ProductDisposition>(snapshot.Disposition, nameof(snapshot.Disposition)),
            snapshot.CreatedAtUtc,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc,
            snapshot.FailureCode,
            snapshot.FailureReason,
            snapshot.Operations.Select(ToAggregate),
            snapshot.RouteDecisions.Select(ToAggregate),
            snapshot.AuditEntries.Select(ToAggregate));
    }

    private static PersistedTraceOperationExecution ToSnapshot(TraceOperationExecution operation)
    {
        return new PersistedTraceOperationExecution(
            operation.OperationRunId,
            operation.OperationId,
            operation.Attempt,
            operation.StationSystemId,
            operation.StationId.Value,
            operation.ProcessDefinitionId.Value,
            operation.ProcessVersionId.Value,
            operation.ConfigurationSnapshotId.Value,
            operation.RecipeSnapshotId.Value,
            operation.RuntimeSessionId?.Value,
            operation.RuntimeSessionStatus?.ToString(),
            operation.ExecutionStatus.ToString(),
            operation.Judgement.ToString(),
            operation.StartedAtUtc,
            operation.CompletedAtUtc,
            operation.FailureCode,
            operation.FailureReason,
            operation.CompletedStepCount,
            operation.CommandCount,
            operation.IncidentCount,
            operation.Commands.Select(ToSnapshot).ToArray(),
            operation.Measurements.Select(ToSnapshot).ToArray(),
            operation.Artifacts.Select(ToSnapshot).ToArray(),
            operation.Incidents.Select(ToSnapshot).ToArray(),
            operation.Outputs.Select(ToSnapshot).ToArray(),
            operation.FencingTokens.Select(ToSnapshot).ToArray());
    }

    private static TraceOperationExecution ToAggregate(PersistedTraceOperationExecution operation)
    {
        return new TraceOperationExecution(
            operation.OperationRunId,
            operation.OperationId,
            operation.Attempt,
            operation.StationSystemId,
            new StationId(operation.StationId),
            new ProcessDefinitionId(operation.ProcessDefinitionId),
            new ProcessVersionId(operation.ProcessVersionId),
            new ConfigurationSnapshotId(operation.ConfigurationSnapshotId),
            new RecipeSnapshotId(operation.RecipeSnapshotId),
            operation.RuntimeSessionId is null ? null : new RuntimeSessionId(operation.RuntimeSessionId.Value),
            operation.RuntimeSessionStatus is null
                ? null
                : ParseEnum<TraceRuntimeSessionStatus>(
                    operation.RuntimeSessionStatus,
                    nameof(operation.RuntimeSessionStatus)),
            ParseEnum<ExecutionStatus>(operation.ExecutionStatus, nameof(operation.ExecutionStatus)),
            ParseEnum<ResultJudgement>(operation.Judgement, nameof(operation.Judgement)),
            operation.StartedAtUtc,
            operation.CompletedAtUtc,
            operation.FailureCode,
            operation.FailureReason,
            operation.CompletedStepCount,
            operation.CommandCount,
            operation.IncidentCount,
            operation.Commands.Select(ToAggregate),
            operation.Measurements.Select(ToAggregate),
            operation.Artifacts.Select(ToAggregate),
            operation.Incidents.Select(ToAggregate),
            operation.Outputs.Select(ToAggregate),
            operation.FencingTokens.Select(ToAggregate));
    }

    private static PersistedTraceRouteDecision ToSnapshot(TraceRouteDecision decision)
    {
        return new PersistedTraceRouteDecision(
            decision.SourceOperationRunId,
            decision.TransitionId,
            decision.TargetOperationId,
            decision.SourceJudgement.ToString(),
            decision.Traversal,
            decision.DecidedAtUtc);
    }

    private static TraceRouteDecision ToAggregate(PersistedTraceRouteDecision decision)
    {
        return new TraceRouteDecision(
            decision.SourceOperationRunId,
            decision.TransitionId,
            decision.TargetOperationId,
            ParseEnum<ResultJudgement>(decision.SourceJudgement, nameof(decision.SourceJudgement)),
            decision.Traversal,
            decision.DecidedAtUtc);
    }

    private static PersistedTraceOperationOutput ToSnapshot(TraceOperationOutput output) =>
        new(output.Key, output.ValueKind, output.CanonicalJson);

    private static TraceOperationOutput ToAggregate(PersistedTraceOperationOutput output) =>
        new(output.Key, output.ValueKind, output.CanonicalJson);

    private static PersistedTraceResourceFencingToken ToSnapshot(TraceResourceFencingToken token) =>
        new(token.ResourceKind, token.ResourceId, token.FencingToken);

    private static TraceResourceFencingToken ToAggregate(PersistedTraceResourceFencingToken token) =>
        new(token.ResourceKind, token.ResourceId, token.FencingToken);

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
            command.ResultJudgement?.ToString(),
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
            command.ResultJudgement is null
                ? null
                : ParseEnum<ResultJudgement>(command.ResultJudgement, nameof(command.ResultJudgement)),
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
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string ActorId,
    string ExecutionStatus,
    string Judgement,
    string Disposition,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    PersistedTraceOperationExecution[] Operations,
    PersistedTraceRouteDecision[] RouteDecisions,
    PersistedAuditEntry[] AuditEntries);

internal sealed record PersistedTraceOperationExecution(
    string OperationRunId,
    string OperationId,
    int Attempt,
    string StationSystemId,
    string StationId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    Guid? RuntimeSessionId,
    string? RuntimeSessionStatus,
    string ExecutionStatus,
    string Judgement,
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
    PersistedTraceIncident[] Incidents,
    PersistedTraceOperationOutput[] Outputs,
    PersistedTraceResourceFencingToken[] FencingTokens);

internal sealed record PersistedTraceRouteDecision(
    string SourceOperationRunId,
    string TransitionId,
    string TargetOperationId,
    string SourceJudgement,
    int Traversal,
    DateTimeOffset DecidedAtUtc);

internal sealed record PersistedTraceOperationOutput(string Key, string ValueKind, string CanonicalJson);

internal sealed record PersistedTraceResourceFencingToken(
    string ResourceKind,
    string ResourceId,
    long FencingToken);

internal sealed record PersistedTraceCommand(
    Guid RuntimeCommandId,
    Guid RuntimeStepId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string TargetCapabilityId,
    string CommandName,
    string Status,
    string? ResultJudgement,
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
