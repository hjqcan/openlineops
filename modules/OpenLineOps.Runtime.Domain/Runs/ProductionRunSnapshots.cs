using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Runs;

public sealed record ProductionStageRunSnapshot(
    string StageId,
    int Sequence,
    string WorkstationId,
    StationId StationId,
    ProcessDefinitionId ProcessDefinitionId,
    ProcessVersionId ProcessVersionId,
    ConfigurationSnapshotId ConfigurationSnapshotId,
    RecipeSnapshotId RecipeSnapshotId,
    ProductionStageRunStatus Status,
    RuntimeSessionId? RuntimeSessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount);

public sealed record ProductionRunSnapshot(
    ProductionRunId RunId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    DutIdentity DutIdentity,
    string? BatchId,
    string? FixtureId,
    string? DeviceId,
    string ActorId,
    ProductionRunStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    IReadOnlyList<ProductionStageRunSnapshot> Stages);
