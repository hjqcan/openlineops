using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenLineOps.Api.Health;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(RabbitMqContainerGroup.Name)]
public sealed class RabbitMqEventBusReadinessIntegrationTests
{
    private readonly RabbitMqContainerFixture _rabbitMq;

    public RabbitMqEventBusReadinessIntegrationTests(RabbitMqContainerFixture rabbitMq)
    {
        _rabbitMq = rabbitMq;
    }

    [RabbitMqIntegrationFact]
    public async Task ReadinessHealthCheckConnectsToConfiguredEventBusRabbitMqBroker()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:EventBus:UseInMemory"] = "false",
                ["OpenLineOps:EventBus:RabbitMq:Enabled"] = "true",
                ["OpenLineOps:EventBus:RabbitMq:HostName"] = _rabbitMq.HostName,
                ["OpenLineOps:EventBus:RabbitMq:Port"] =
                    _rabbitMq.Port.ToString(CultureInfo.InvariantCulture),
                ["OpenLineOps:EventBus:RabbitMq:UserName"] = _rabbitMq.UserName,
                ["OpenLineOps:EventBus:RabbitMq:Password"] = _rabbitMq.Password,
                ["OpenLineOps:EventBus:RabbitMq:VirtualHost"] = _rabbitMq.VirtualHost,
                ["OpenLineOps:EventBus:RabbitMq:ExchangeName"] =
                    "openlineops.events.integration-test"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsReadinessHealthChecks(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var healthCheckService = serviceProvider.GetRequiredService<HealthCheckService>();
        var report = await healthCheckService.CheckHealthAsync(
            registration => registration.Tags.Contains("rabbitmq"));

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.True(report.Entries.ContainsKey("openlineops.eventbus.rabbitmq"));
        Assert.Equal(
            HealthStatus.Healthy,
            report.Entries["openlineops.eventbus.rabbitmq"].Status);
    }
}
