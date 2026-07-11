namespace OpenLineOps.Runtime.Infrastructure.Transport;

public sealed class StationCoordinatorTransportOptions
{
    public const string SectionName = "OpenLineOps:Runtime:AgentTransport";

    public string Provider { get; set; } = StationCoordinatorTransportProviders.RabbitMq;

    public string? BrokerUri { get; set; }

    public string CoordinatorId { get; set; } = Environment.MachineName.ToLowerInvariant();

    public bool RequireTls { get; set; } = true;

    public TimeSpan SafetyAcknowledgementTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public string JobExchange { get; set; } = "openlineops.station.jobs";

    public string EventExchange { get; set; } = "openlineops.station.events";

    public string SafetyCommandExchange { get; set; } = "openlineops.station.safety";

    public string SafetyEventExchange { get; set; } = "openlineops.station.safety-events";

    public List<StationDeploymentOptions> Deployments { get; set; } = [];

    public Uri ResolveBrokerUri()
    {
        if (!Uri.TryCreate(BrokerUri, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException(
                $"{SectionName}:BrokerUri must be an absolute amqp or amqps URI.");
        }

        if (RequireTls && !string.Equals(uri.Scheme, "amqps", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}:BrokerUri must use amqps when RequireTls is true.");
        }

        return uri;
    }
}

public sealed class StationDeploymentOptions
{
    public string ProjectId { get; set; } = string.Empty;

    public string ApplicationId { get; set; } = string.Empty;

    public string ProjectSnapshotId { get; set; } = string.Empty;

    public string StationSystemId { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string StationId { get; set; } = string.Empty;

    public string PackageContentSha256 { get; set; } = string.Empty;
}

public static class StationCoordinatorTransportProviders
{
    public const string RabbitMq = "RabbitMq";
    public const string Disabled = "Disabled";

    public static StationCoordinatorTransportProvider Parse(string? value) => value switch
    {
        RabbitMq => StationCoordinatorTransportProvider.RabbitMq,
        Disabled => StationCoordinatorTransportProvider.Disabled,
        _ => throw new InvalidOperationException(
            $"Unsupported Station coordinator transport provider '{value}'. Expected exactly "
            + $"'{RabbitMq}' or '{Disabled}'.")
    };
}

public enum StationCoordinatorTransportProvider
{
    RabbitMq,
    Disabled
}
