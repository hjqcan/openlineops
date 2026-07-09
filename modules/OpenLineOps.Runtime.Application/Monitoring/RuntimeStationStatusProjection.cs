using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Monitoring;

public sealed record RuntimeStationStatusProjection(
    string StationId,
    RuntimeSessionId LatestSessionId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string? SerialNumber,
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
