namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeStationStatusResponse(
    string StationId,
    Guid LatestSessionId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string? SerialNumber,
    string? BatchId,
    string? FixtureId,
    string? DeviceId,
    string SessionStatus,
    int StepCount,
    int CompletedStepCount,
    int RunningStepCount,
    int CommandCount,
    int IncidentCount,
    DateTimeOffset LastTransitionAtUtc,
    bool IsTerminal);
