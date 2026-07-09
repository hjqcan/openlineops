namespace OpenLineOps.Runtime.Domain.Sessions;

public sealed record RuntimeSessionTraceMetadata
{
    public static RuntimeSessionTraceMetadata Empty { get; } = new(null, null, null, null, null);

    public RuntimeSessionTraceMetadata(
        string? serialNumber,
        string? batchId,
        string? fixtureId,
        string? deviceId,
        string? actorId,
        string? projectId = null,
        string? applicationId = null,
        string? projectSnapshotId = null,
        string? topologyId = null)
    {
        SerialNumber = NormalizeOptional(serialNumber);
        BatchId = NormalizeOptional(batchId);
        FixtureId = NormalizeOptional(fixtureId);
        DeviceId = NormalizeOptional(deviceId);
        ActorId = NormalizeOptional(actorId);
        ProjectId = NormalizeOptional(projectId);
        ApplicationId = NormalizeOptional(applicationId);
        ProjectSnapshotId = NormalizeOptional(projectSnapshotId);
        TopologyId = NormalizeOptional(topologyId);
    }

    public string? SerialNumber { get; }

    public string? BatchId { get; }

    public string? FixtureId { get; }

    public string? DeviceId { get; }

    public string? ActorId { get; }

    public string? ProjectId { get; }

    public string? ApplicationId { get; }

    public string? ProjectSnapshotId { get; }

    public string? TopologyId { get; }

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
}
