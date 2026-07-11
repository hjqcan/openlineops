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
    string OperationId,
    int OperationAttempt,
    string StationSystemId,
    ProductionUnitIdentity ProductionUnitIdentity,
    string RuntimeStationId,
    RuntimeSessionId LatestSessionId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string? LotId,
    string? CarrierId,
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
