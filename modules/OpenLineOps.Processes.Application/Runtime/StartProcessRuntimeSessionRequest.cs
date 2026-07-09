namespace OpenLineOps.Processes.Application.Runtime;

public sealed record StartProcessRuntimeSessionRequest(
    string ConfigurationSnapshotId,
    string? SerialNumber = null,
    string? BatchId = null,
    string? FixtureId = null,
    string? DeviceId = null,
    string? ActorId = null);
