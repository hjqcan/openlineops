namespace OpenLineOps.Runtime.Api.Models;

public sealed record StartSimulatedRuntimeSessionRequest(
    string StationId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    IReadOnlyList<StartSimulatedRuntimeNodeRequest> Nodes,
    string? SerialNumber = null,
    string? BatchId = null,
    string? FixtureId = null,
    string? DeviceId = null,
    string? ActorId = null);
