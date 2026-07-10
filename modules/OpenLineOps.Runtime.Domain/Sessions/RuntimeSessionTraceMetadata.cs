namespace OpenLineOps.Runtime.Domain.Sessions;

public sealed record RuntimeSessionTraceMetadata
{
    public RuntimeSessionTraceMetadata(
        string? serialNumber,
        string? batchId,
        string? fixtureId,
        string? deviceId,
        string? actorId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId)
    {
        SerialNumber = NormalizeOptional(serialNumber);
        BatchId = NormalizeOptional(batchId);
        FixtureId = NormalizeOptional(fixtureId);
        DeviceId = NormalizeOptional(deviceId);
        ActorId = NormalizeOptional(actorId);
        ProjectId = Required(projectId, nameof(projectId));
        ApplicationId = Required(applicationId, nameof(applicationId));
        ProjectSnapshotId = Required(projectSnapshotId, nameof(projectSnapshotId));
        TopologyId = Required(topologyId, nameof(topologyId));
    }

    public string? SerialNumber { get; }

    public string? BatchId { get; }

    public string? FixtureId { get; }

    public string? DeviceId { get; }

    public string? ActorId { get; }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public string TopologyId { get; }

    public bool CanCreateTraceRecord =>
        SerialNumber is not null
        && DeviceId is not null
        && ActorId is not null;

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} cannot be empty.", parameterName)
            : value.Trim();
    }
}
