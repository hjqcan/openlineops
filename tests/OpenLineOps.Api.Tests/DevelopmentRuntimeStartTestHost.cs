using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenLineOps.Api.Abstractions;

namespace OpenLineOps.Api.Tests;

internal static class DevelopmentRuntimeStartTestHost
{
    public static WebApplicationFactory<Program> Create(
        WebApplicationFactory<Program> factory,
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                var configuration = new Dictionary<string, string?>
                {
                    [DevelopmentRuntimeStartPolicy.EnabledConfigurationKey] = "true"
                };

                if (additionalConfiguration is not null)
                {
                    foreach (var (key, value) in additionalConfiguration)
                    {
                        configuration[key] = value;
                    }
                }

                configurationBuilder.AddInMemoryCollection(configuration);
            });
        });
    }
}
