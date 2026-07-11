using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;
using OpenLineOps.Runtime.Domain.Steps;
using OpenLineOps.Runtime.Domain.Targets;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class RuntimeSessionSnapshotMapper
{
    internal const int CurrentSchemaVersion = 1;
    internal const string CurrentResourceKind = "OpenLineOps.RuntimeSession";

    public static PersistedRuntimeSession ToSnapshot(RuntimeSession session)
    {
        return new PersistedRuntimeSession(
            CurrentSchemaVersion,
            CurrentResourceKind,
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
        RequireCurrentSchema(snapshot);

        var steps = snapshot.Steps.Select(ToAggregate).ToArray();

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
            steps,
            snapshot.Commands.Select(ToAggregate),
            snapshot.Incidents.Select(ToAggregate),
            ToAggregate(snapshot.TraceMetadata));
    }

    private static PersistedRuntimeTraceMetadata ToSnapshot(RuntimeSessionTraceMetadata traceMetadata)
    {
        return new PersistedRuntimeTraceMetadata(
            traceMetadata.ProductionRunId.Value,
            traceMetadata.ProductionUnitId.Value,
            traceMetadata.ProductionLineDefinitionId,
            traceMetadata.OperationId,
            traceMetadata.OperationRunId,
            traceMetadata.OperationAttempt,
            traceMetadata.StationSystemId,
            traceMetadata.ProductionUnitIdentity.ModelId,
            traceMetadata.ProductionUnitIdentity.InputKey,
            traceMetadata.ProductionUnitIdentity.Value,
            traceMetadata.LotId,
            traceMetadata.CarrierId,
            traceMetadata.FixtureId,
            traceMetadata.DeviceId,
            traceMetadata.ActorId,
            traceMetadata.ProjectId,
            traceMetadata.ApplicationId,
            traceMetadata.ProjectSnapshotId,
            traceMetadata.TopologyId,
            traceMetadata.ResourceLeaseFences.Select(fence => new PersistedResourceLeaseFence(
                fence.Resource.Kind.ToString(),
                fence.Resource.ResourceId,
                fence.FencingToken,
                fence.ExpiresAtUtc)).ToArray());
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
            step.FailureReason,
            step.ActionId.Value,
            step.TargetKind,
            step.TargetId);
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
            command.FailureReason,
            command.ResultJudgement?.ToString(),
            command.ActionId.Value,
            command.TargetKind,
            command.TargetId);
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
            step.FailureReason,
            RequiredActionId(step.ActionId, $"runtime step {step.StepId:D}"),
            RequiredTarget(step.TargetKind, step.TargetId, $"runtime step {step.StepId:D}"));
    }

    private static RuntimeCommand ToAggregate(PersistedRuntimeCommand command)
    {
        return RuntimeCommand.Restore(
            new RuntimeCommandId(command.CommandId),
            new RuntimeStepId(command.StepId),
            new RuntimeCapabilityId(command.TargetCapabilityId),
            command.CommandName,
            ParseEnum<OpenLineOps.Runtime.Domain.Commands.RuntimeCommandStatus>(
                command.Status,
                nameof(command.Status)),
            command.CreatedAtUtc,
            command.Timeout,
            command.AcceptedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultPayload,
            command.FailureReason,
            ParseOptionalEnum<ResultJudgement>(
                command.ResultJudgement,
                nameof(command.ResultJudgement)),
            RequiredActionId(command.ActionId, $"runtime command {command.CommandId:D}"),
            RequiredTarget(command.TargetKind, command.TargetId, $"runtime command {command.CommandId:D}"));
    }

    private static RuntimeActionId RequiredActionId(string? value, string owner)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Persisted {owner} does not declare an ActionId.");
        }

        return new RuntimeActionId(value);
    }

    private static RuntimeTargetReference RequiredTarget(
        string? kind,
        string? targetId,
        string owner)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(targetId))
        {
            throw new InvalidOperationException($"Persisted {owner} does not declare TargetKind and TargetId.");
        }

        return new RuntimeTargetReference(kind, targetId);
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
        if (traceMetadata is null)
        {
            throw new InvalidDataException(
                "Persisted runtime session does not declare release trace metadata.");
        }

        return new RuntimeSessionTraceMetadata(
            RequiredProductionRunId(traceMetadata.ProductionRunId),
            traceMetadata.ProductionUnitId == Guid.Empty
                ? throw new InvalidDataException(
                    "Persisted runtime session trace metadata does not declare Production Unit id.")
                : new ProductionUnitId(traceMetadata.ProductionUnitId),
            RequiredTraceIdentity(
                traceMetadata.ProductionLineDefinitionId,
                "production line definition id"),
            RequiredTraceIdentity(traceMetadata.OperationId, "operation id"),
            RequiredTraceIdentity(traceMetadata.OperationRunId, "operation run id"),
            RequiredPositiveAttempt(traceMetadata.OperationAttempt),
            RequiredTraceIdentity(traceMetadata.StationSystemId, "station system id"),
            new ProductionUnitIdentity(
                RequiredTraceIdentity(traceMetadata.ProductModelId, "product model id"),
                RequiredTraceIdentity(
                    traceMetadata.ProductionUnitIdentityInputKey,
                    "Production Unit identity input key"),
                RequiredTraceIdentity(
                    traceMetadata.ProductionUnitIdentityValue,
                    "Production Unit identity value")),
            traceMetadata.LotId,
            traceMetadata.CarrierId,
            traceMetadata.FixtureId,
            traceMetadata.DeviceId,
            RequiredTraceIdentity(traceMetadata.ActorId, "actor id"),
            RequiredTraceIdentity(traceMetadata.ProjectId, "project id"),
            RequiredTraceIdentity(traceMetadata.ApplicationId, "application id"),
            RequiredTraceIdentity(traceMetadata.ProjectSnapshotId, "project snapshot id"),
            RequiredTraceIdentity(traceMetadata.TopologyId, "topology id"),
            (traceMetadata.ResourceLeaseFences
                ?? throw new InvalidDataException(
                    "Persisted runtime session trace metadata does not declare resource lease fences."))
                .Select(fence => new ResourceLeaseFenceEvidence(
                    new ResourceRequirement(
                        ParseEnum<ResourceKind>(fence.ResourceKind, nameof(fence.ResourceKind)),
                        RequiredTraceIdentity(fence.ResourceId, "resource id")),
                    fence.FencingToken,
                    fence.ExpiresAtUtc)));
    }

    private static void RequireCurrentSchema(PersistedRuntimeSession snapshot)
    {
        if (snapshot.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(snapshot.ResourceKind, CurrentResourceKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Persisted runtime session schema is not current. Expected resource kind "
                + $"'{CurrentResourceKind}' and schema version {CurrentSchemaVersion}.");
        }
    }

    private static ProductionRunId RequiredProductionRunId(Guid value)
    {
        return value == Guid.Empty
            ? throw new InvalidDataException(
                "Persisted runtime session trace metadata does not declare production run id.")
            : new ProductionRunId(value);
    }

    private static int RequiredPositiveAttempt(int value)
    {
        return value <= 0
            ? throw new InvalidDataException(
                "Persisted runtime session trace metadata does not declare a positive operation attempt.")
            : value;
    }

    private static string RequiredTraceIdentity(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException(
                $"Persisted runtime session trace metadata does not declare {fieldName}.")
            : value;
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum
    {
        if (CanonicalEnumToken.TryParse<TEnum>(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Persisted {fieldName} value '{value}' is invalid. " +
            $"Expected an exact, case-sensitive {typeof(TEnum).Name} token: " +
            $"{CanonicalEnumToken.ExpectedTokens<TEnum>()}.");
    }

    private static TEnum? ParseOptionalEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        return value is null ? null : ParseEnum<TEnum>(value, fieldName);
    }
}

internal sealed record PersistedRuntimeSession(
    int SchemaVersion,
    string ResourceKind,
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
    Guid ProductionRunId,
    Guid ProductionUnitId,
    string? ProductionLineDefinitionId,
    string? OperationId,
    string? OperationRunId,
    int OperationAttempt,
    string? StationSystemId,
    string? ProductModelId,
    string? ProductionUnitIdentityInputKey,
    string? ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string? FixtureId,
    string? DeviceId,
    string? ActorId,
    string? ProjectId,
    string? ApplicationId,
    string? ProjectSnapshotId,
    string? TopologyId,
    PersistedResourceLeaseFence[]? ResourceLeaseFences);

internal sealed record PersistedResourceLeaseFence(
    string ResourceKind,
    string ResourceId,
    long FencingToken,
    DateTimeOffset ExpiresAtUtc);

internal sealed record PersistedRuntimeStep(
    Guid StepId,
    string NodeId,
    string DisplayName,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureReason,
    string ActionId,
    string TargetKind,
    string TargetId);

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
    string? FailureReason,
    string? ResultJudgement,
    string ActionId,
    string TargetKind,
    string TargetId);

internal sealed record PersistedRuntimeIncident(
    Guid IncidentId,
    string Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc);
