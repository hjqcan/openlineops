namespace OpenLineOps.EventBus.Configuration;

public sealed class OpenLineOpsEventBusOptions
{
    public const string SectionName = "OpenLineOps:EventBus";

    public string? PublicationMode { get; set; }

    public bool UseInMemory { get; set; } = true;

    public string ConnectionStringName { get; set; } = "OpenLineOpsEventBus";

    public string PostgreSqlSchema { get; set; } = "cap";

    public bool UseDashboard { get; set; }

    public int FailedRetryCount { get; set; } = 3;

    public int FailedRetryIntervalSeconds { get; set; } = 60;

    public int SucceedMessageExpiredAfterSeconds { get; set; } = 24 * 3600;

    public int FailedMessageExpiredAfterSeconds { get; set; } = 15 * 24 * 3600;

    public int ConsumerThreadCount { get; set; } = 1;

    public RabbitMqEventBusOptions RabbitMq { get; set; } = new();
}

public sealed class RabbitMqEventBusOptions
{
    public bool Enabled { get; set; }

    public string HostName { get; set; } = "localhost";

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string VirtualHost { get; set; } = "/";

    public string ExchangeName { get; set; } = "openlineops.events";

    public int Port { get; set; } = 5672;
}
