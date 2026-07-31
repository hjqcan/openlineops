namespace OpenLineOps.Integration.Api.Transport;

public sealed class IntegrationOutboxWorkerOptions
{
    public const string SectionName = "OpenLineOps:Integration:OutboxWorker";

    public int BatchSize { get; set; } = 100;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ConnectorUnavailableInterval { get; set; } =
        TimeSpan.FromSeconds(5);

    public TimeSpan FailureInterval { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsValid =>
        BatchSize is >= 1 and <= 1000
        && IsValidInterval(PollInterval)
        && IsValidInterval(ConnectorUnavailableInterval)
        && IsValidInterval(FailureInterval);

    private static bool IsValidInterval(TimeSpan value) =>
        value >= TimeSpan.FromMilliseconds(10)
        && value <= TimeSpan.FromMinutes(5)
        && value.Ticks % TimeSpan.TicksPerMillisecond == 0;
}
