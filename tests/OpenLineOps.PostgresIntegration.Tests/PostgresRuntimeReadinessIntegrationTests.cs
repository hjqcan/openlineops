using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenLineOps.Api.Health;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresRuntimeReadinessIntegrationTests(PostgresContainerFixture postgres)
{
    [PostgresIntegrationFact]
    public async Task ReadinessConnectsToAuthoritativeRuntimeCoordinationDatabase()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ProductionCoordinationPersistenceOptions.SectionName}:Provider"] =
                    ProductionCoordinationPersistenceProviders.PostgreSql,
                [$"{ProductionCoordinationPersistenceOptions.SectionName}:ConnectionString"] =
                    postgres.ConnectionString
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsReadinessHealthChecks(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var report = await serviceProvider
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration =>
                registration.Tags.Contains("runtime")
                && registration.Tags.Contains("coordination"));

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(
            HealthStatus.Healthy,
            report.Entries["openlineops.runtime.coordination.postgresql"].Status);
    }
}
