using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed record RuntimeStationStatusProjection(
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    ProductionRunId ProductionRunId,
    string ProductionLineDefinitionId,
    string StageId,
    int StageSequence,
    string WorkstationId,
    DutIdentity DutIdentity,
    string StationSystemId,
    RuntimeSessionId LatestSessionId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string? BatchId,
    string? FixtureId,
    string? DeviceId,
    RuntimeSessionStatus SessionStatus,
    int StepCount,
    int CompletedStepCount,
    int RunningStepCount,
    int CommandCount,
    int IncidentCount,
    DateTimeOffset LastTransitionAtUtc,
    bool IsTerminal);
