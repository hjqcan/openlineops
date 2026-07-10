using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class ProductionRunSnapshotMapper
{
    internal const int CurrentSchemaVersion = 1;
    internal const string CurrentResourceKind = "OpenLineOps.ProductionRun";

    public static PersistedProductionRun ToSnapshot(ProductionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var snapshot = run.ToSnapshot();
        return new PersistedProductionRun(
            CurrentSchemaVersion,
            CurrentResourceKind,
            snapshot.RunId.Value,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.ProjectSnapshotId,
            snapshot.TopologyId,
            snapshot.ProductionLineDefinitionId,
            snapshot.DutIdentity.ModelId,
            snapshot.DutIdentity.InputKey,
            snapshot.DutIdentity.Value,
            snapshot.BatchId,
            snapshot.FixtureId,
            snapshot.DeviceId,
            snapshot.ActorId,
            snapshot.Status.ToString(),
            snapshot.CreatedAtUtc,
            snapshot.LastTransitionAtUtc,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc,
            snapshot.FailureCode,
            snapshot.FailureReason,
            snapshot.Stages.Select(ToSnapshot).ToArray());
    }

    public static ProductionRun ToAggregate(PersistedProductionRun snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireCurrentSchema(snapshot);
        if (snapshot.RunId == Guid.Empty)
        {
            throw new InvalidDataException("Persisted production run does not declare run id.");
        }

        if (snapshot.Stages is null)
        {
            throw new InvalidDataException("Persisted production run does not declare stages.");
        }

        var runSnapshot = new ProductionRunSnapshot(
            new ProductionRunId(snapshot.RunId),
            Required(snapshot.ProjectId, "project id"),
            Required(snapshot.ApplicationId, "application id"),
            Required(snapshot.ProjectSnapshotId, "project snapshot id"),
            Required(snapshot.TopologyId, "topology id"),
            Required(snapshot.ProductionLineDefinitionId, "production line definition id"),
            new DutIdentity(
                Required(snapshot.DutModelId, "DUT model id"),
                Required(snapshot.DutIdentityInputKey, "DUT identity input key"),
                Required(snapshot.DutIdentityValue, "DUT identity value")),
            Optional(snapshot.BatchId, "batch id"),
            Optional(snapshot.FixtureId, "fixture id"),
            Optional(snapshot.DeviceId, "device id"),
            Required(snapshot.ActorId, "actor id"),
            ParseEnum<ProductionRunStatus>(snapshot.Status, "status"),
            snapshot.CreatedAtUtc,
            snapshot.LastTransitionAtUtc,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc,
            Optional(snapshot.FailureCode, "failure code"),
            Optional(snapshot.FailureReason, "failure reason"),
            snapshot.Stages.Select(ToAggregate).ToArray());
        return ProductionRun.Restore(runSnapshot);
    }

    private static PersistedProductionStageRun ToSnapshot(ProductionStageRunSnapshot stage)
    {
        return new PersistedProductionStageRun(
            stage.StageId,
            stage.Sequence,
            stage.WorkstationId,
            stage.StationId.Value,
            stage.ProcessDefinitionId.Value,
            stage.ProcessVersionId.Value,
            stage.ConfigurationSnapshotId.Value,
            stage.RecipeSnapshotId.Value,
            stage.Status.ToString(),
            stage.RuntimeSessionId?.Value,
            stage.StartedAtUtc,
            stage.CompletedAtUtc,
            stage.FailureCode,
            stage.FailureReason,
            stage.CompletedStepCount,
            stage.CommandCount,
            stage.IncidentCount);
    }

    private static ProductionStageRunSnapshot ToAggregate(PersistedProductionStageRun stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Sequence <= 0)
        {
            throw new InvalidDataException(
                $"Persisted production stage {stage.StageId} does not declare a positive sequence.");
        }

        if (stage.RuntimeSessionId == Guid.Empty)
        {
            throw new InvalidDataException(
                $"Persisted production stage {stage.StageId} declares an empty runtime session id.");
        }

        return new ProductionStageRunSnapshot(
            Required(stage.StageId, "stage id"),
            stage.Sequence,
            Required(stage.WorkstationId, "workstation id"),
            new StationId(Required(stage.StationId, "station id")),
            new ProcessDefinitionId(Required(stage.ProcessDefinitionId, "process definition id")),
            new ProcessVersionId(Required(stage.ProcessVersionId, "process version id")),
            new ConfigurationSnapshotId(
                Required(stage.ConfigurationSnapshotId, "configuration snapshot id")),
            new RecipeSnapshotId(Required(stage.RecipeSnapshotId, "recipe snapshot id")),
            ParseEnum<ProductionStageRunStatus>(stage.Status, "stage status"),
            stage.RuntimeSessionId is null
                ? null
                : new RuntimeSessionId(stage.RuntimeSessionId.Value),
            stage.StartedAtUtc,
            stage.CompletedAtUtc,
            Optional(stage.FailureCode, "stage failure code"),
            Optional(stage.FailureReason, "stage failure reason"),
            stage.CompletedStepCount,
            stage.CommandCount,
            stage.IncidentCount);
    }

    private static void RequireCurrentSchema(PersistedProductionRun snapshot)
    {
        if (snapshot.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(snapshot.ResourceKind, CurrentResourceKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Persisted production run schema is not current. Expected resource kind "
                + $"'{CurrentResourceKind}' and schema version {CurrentSchemaVersion}.");
        }
    }

    private static string Required(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException(
                $"Persisted production run does not declare canonical {fieldName}.")
            : value;
    }

    private static string? Optional(string? value, string fieldName)
    {
        return value is null ? null : Required(value, fieldName);
    }

    private static TEnum ParseEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        if (value is not null && CanonicalEnumToken.TryParse<TEnum>(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Persisted production run {fieldName} value '{value}' is invalid. "
            + $"Expected an exact, case-sensitive {typeof(TEnum).Name} token: "
            + $"{CanonicalEnumToken.ExpectedTokens<TEnum>()}.");
    }
}

internal sealed record PersistedProductionRun(
    int SchemaVersion,
    string? ResourceKind,
    Guid RunId,
    string? ProjectId,
    string? ApplicationId,
    string? ProjectSnapshotId,
    string? TopologyId,
    string? ProductionLineDefinitionId,
    string? DutModelId,
    string? DutIdentityInputKey,
    string? DutIdentityValue,
    string? BatchId,
    string? FixtureId,
    string? DeviceId,
    string? ActorId,
    string? Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    PersistedProductionStageRun[]? Stages);

internal sealed record PersistedProductionStageRun(
    string? StageId,
    int Sequence,
    string? WorkstationId,
    string? StationId,
    string? ProcessDefinitionId,
    string? ProcessVersionId,
    string? ConfigurationSnapshotId,
    string? RecipeSnapshotId,
    string? Status,
    Guid? RuntimeSessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount);
