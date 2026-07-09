using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class RuntimeSessionSnapshotMapper
{
    public static PersistedRuntimeSession ToSnapshot(RuntimeSession session)
    {
        return new PersistedRuntimeSession(
            session.Id.Value,
            session.StationId.Value,
            session.ProcessDefinitionId.Value,
            session.ProcessVersionId.Value,
            session.ConfigurationSnapshotId.Value,
            session.RecipeSnapshotId.Value,
            ToSnapshot(session.TraceMetadata),
            session.Status.ToString(),
            session.CreatedAtUtc,
            session.LastTransitionAtUtc,
            session.StartedAtUtc,
            session.PausedAtUtc,
            session.CompletedAtUtc,
            session.Steps.Select(ToSnapshot).ToArray(),
            session.Commands.Select(ToSnapshot).ToArray(),
            session.Incidents.Select(ToSnapshot).ToArray());
    }

    public static RuntimeSession ToAggregate(PersistedRuntimeSession snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return RuntimeSession.Restore(
            new RuntimeSessionId(snapshot.SessionId),
            new StationId(snapshot.StationId),
            new ProcessDefinitionId(snapshot.ProcessDefinitionId),
            new ProcessVersionId(snapshot.ProcessVersionId),
            new ConfigurationSnapshotId(snapshot.ConfigurationSnapshotId),
            new RecipeSnapshotId(snapshot.RecipeSnapshotId),
            ParseEnum<RuntimeSessionStatus>(snapshot.Status, nameof(snapshot.Status)),
            snapshot.CreatedAtUtc,
            snapshot.LastTransitionAtUtc,
            snapshot.StartedAtUtc,
            snapshot.PausedAtUtc,
            snapshot.CompletedAtUtc,
            snapshot.Steps.Select(ToAggregate),
            snapshot.Commands.Select(ToAggregate),
            snapshot.Incidents.Select(ToAggregate),
            ToAggregate(snapshot.TraceMetadata));
    }

    private static PersistedRuntimeTraceMetadata ToSnapshot(RuntimeSessionTraceMetadata traceMetadata)
    {
        return new PersistedRuntimeTraceMetadata(
            traceMetadata.SerialNumber,
            traceMetadata.BatchId,
            traceMetadata.FixtureId,
            traceMetadata.DeviceId,
            traceMetadata.ActorId);
    }

    private static PersistedRuntimeStep ToSnapshot(RuntimeStep step)
    {
        return new PersistedRuntimeStep(
            step.Id.Value,
            step.NodeId.Value,
            step.DisplayName,
            step.Status.ToString(),
            step.StartedAtUtc,
            step.CompletedAtUtc,
            step.FailureReason);
    }

    private static PersistedRuntimeCommand ToSnapshot(RuntimeCommand command)
    {
        return new PersistedRuntimeCommand(
            command.Id.Value,
            command.StepId.Value,
            command.TargetCapability.Value,
            command.CommandName,
            command.Status.ToString(),
            command.CreatedAtUtc,
            command.Timeout,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason);
    }

    private static PersistedRuntimeIncident ToSnapshot(RuntimeIncident incident)
    {
        return new PersistedRuntimeIncident(
            incident.Id.Value,
            incident.Severity.ToString(),
            incident.Code,
            incident.Message,
            incident.OccurredAtUtc);
    }

    private static RuntimeStep ToAggregate(PersistedRuntimeStep step)
    {
        return RuntimeStep.Restore(
            new RuntimeStepId(step.StepId),
            new RuntimeNodeId(step.NodeId),
            step.DisplayName,
            ParseEnum<RuntimeStepStatus>(step.Status, nameof(step.Status)),
            step.StartedAtUtc,
            step.CompletedAtUtc,
            step.FailureReason);
    }

    private static RuntimeCommand ToAggregate(PersistedRuntimeCommand command)
    {
        return RuntimeCommand.Restore(
            new RuntimeCommandId(command.CommandId),
            new RuntimeStepId(command.StepId),
            new RuntimeCapabilityId(command.TargetCapabilityId),
            command.CommandName,
            ParseEnum<RuntimeCommandStatus>(command.Status, nameof(command.Status)),
            command.CreatedAtUtc,
            command.Timeout,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason);
    }

    private static RuntimeIncident ToAggregate(PersistedRuntimeIncident incident)
    {
        return RuntimeIncident.Record(
            new RuntimeIncidentId(incident.IncidentId),
            ParseEnum<RuntimeIncidentSeverity>(incident.Severity, nameof(incident.Severity)),
            incident.Code,
            incident.Message,
            incident.OccurredAtUtc);
    }

    private static RuntimeSessionTraceMetadata ToAggregate(PersistedRuntimeTraceMetadata? traceMetadata)
    {
        return traceMetadata is null
            ? RuntimeSessionTraceMetadata.Empty
            : new RuntimeSessionTraceMetadata(
                traceMetadata.SerialNumber,
                traceMetadata.BatchId,
                traceMetadata.FixtureId,
                traceMetadata.DeviceId,
                traceMetadata.ActorId);
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

internal sealed record PersistedRuntimeSession(
    Guid SessionId,
    string StationId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    PersistedRuntimeTraceMetadata? TraceMetadata,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? PausedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    PersistedRuntimeStep[] Steps,
    PersistedRuntimeCommand[] Commands,
    PersistedRuntimeIncident[] Incidents);

internal sealed record PersistedRuntimeTraceMetadata(
    string? SerialNumber,
    string? BatchId,
    string? FixtureId,
    string? DeviceId,
    string? ActorId);

internal sealed record PersistedRuntimeStep(
    Guid StepId,
    string NodeId,
    string DisplayName,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureReason);

internal sealed record PersistedRuntimeCommand(
    Guid CommandId,
    Guid StepId,
    string TargetCapabilityId,
    string CommandName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    TimeSpan Timeout,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultPayload,
    string? FailureReason);

internal sealed record PersistedRuntimeIncident(
    Guid IncidentId,
    string Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc);
