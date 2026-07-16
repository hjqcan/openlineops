using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Plugins.Api.DependencyInjection;
using OpenLineOps.Plugins.Application.Extensions;
using OpenLineOps.Plugins.Application.Lifecycle;
using OpenLineOps.Plugins.Infrastructure.Extensions;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Api.Tests;

public sealed class PluginsModuleDependencyInjectionTests
{
    [Fact]
    public void AddOpenLineOpsPluginsModuleRegistersOnlyExternalProcessActivation()
    {
        var services = new ServiceCollection();
        services.AddOpenLineOpsPluginsModule();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ExternalProcessPluginInstanceActivator>(
            provider.GetRequiredService<IPluginInstanceActivator>());
        Assert.Null(provider.GetService<IApplicationExtensionPackageService>());
    }

    [Fact]
    public void AddOpenLineOpsPluginsApiRegistersApplicationExtensionAuthoringService()
    {
        var services = new ServiceCollection();

        services.AddControllers().AddOpenLineOpsPluginsApi();

        var descriptor = Assert.Single(
            services,
            candidate => candidate.ServiceType == typeof(IApplicationExtensionPackageService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(FileSystemApplicationExtensionPackageService), descriptor.ImplementationType);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("None")]
    [InlineData("Memory")]
    [InlineData("")]
    public void AddOpenLineOpsPluginsModuleRejectsNonCanonicalEventLogProvider(string provider)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AddModule(("EventLog:Provider", provider)));

        Assert.Contains("Expected exactly 'Sqlite'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    public void AddOpenLineOpsPluginsModuleRejectsInvalidBoolean(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AddModule(("RegisterRoutingInventories", value)));

        Assert.Contains("exactly 'true' or 'false'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddOpenLineOpsPluginsModuleRejectsInvalidExternalHostDuration()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AddModule(("ExternalHost:StartupProbeDelay", "immediate")));

        Assert.Contains("valid TimeSpan", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("externalprocess")]
    [InlineData("Docker")]
    [InlineData("Podman")]
    [InlineData("RunAs")]
    [InlineData("")]
    public void AddOpenLineOpsPluginsModuleRejectsNonCanonicalSandboxIsolationMode(string isolationMode)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AddModule(("ExternalHost:Sandbox:IsolationMode", isolationMode)));

        Assert.Contains("Expected exactly", exception.Message, StringComparison.Ordinal);
    }

    private static void AddModule(params (string Key, string Value)[] values)
    {
        var configurationValues = values.ToDictionary(
            item => $"{PluginsModuleOptions.SectionName}:{item.Key}",
            item => (string?)item.Value,
            StringComparer.Ordinal);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        new ServiceCollection().AddOpenLineOpsPluginsModule(configuration);
    }
}
