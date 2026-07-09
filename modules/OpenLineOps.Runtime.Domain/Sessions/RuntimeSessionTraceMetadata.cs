namespace OpenLineOps.Runtime.Domain.Sessions;

public sealed record RuntimeSessionTraceMetadata
{
    public static RuntimeSessionTraceMetadata Empty { get; } = new(null, null, null, null, null);

    public RuntimeSessionTraceMetadata(
        string? serialNumber,
        string? batchId,
        string? fixtureId,
        string? deviceId,
        string? actorId)
    {
        SerialNumber = NormalizeOptional(serialNumber);
        BatchId = NormalizeOptional(batchId);
        FixtureId = NormalizeOptional(fixtureId);
        DeviceId = NormalizeOptional(deviceId);
        ActorId = NormalizeOptional(actorId);
    }

    public string? SerialNumber { get; }

    public string? BatchId { get; }

    public string? FixtureId { get; }

    public string? DeviceId { get; }

    public string? ActorId { get; }

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
