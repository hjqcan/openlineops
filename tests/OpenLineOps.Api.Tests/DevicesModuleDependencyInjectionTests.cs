using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Devices.Api.DependencyInjection;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Devices.Infrastructure.Persistence;
using OpenLineOps.Devices.Infrastructure.Persistence.Ef;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Infrastructure.Scripting;

namespace OpenLineOps.Api.Tests;

public sealed class DevicesModuleDependencyInjectionTests
{
    [Fact]
    public async Task AddOpenLineOpsDevicesModuleRegistersReleaseBoundSimulatorWithoutGlobalSelector()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPluginDeviceCommandInvoker>(
            new StaticPluginDeviceCommandInvoker(
                PluginDeviceCommandInvocationResult.Completed("unused")));
        services.AddOpenLineOpsDevicesModule();

        using var serviceProvider = services.BuildServiceProvider();
        var executor = serviceProvider.GetRequiredService<IDeviceCommandExecutor>();

        Assert.IsType<ProjectReleaseDeviceCommandExecutor>(executor);
        var result = await executor.ExecuteAsync(CreateSimulatorRequest());

        Assert.Equal(DeviceCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Contains("simulator://scanner-01", result.ResultPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddOpenLineOpsDevicesModuleRoutesFrozenPluginPackageWithoutInventory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPluginDeviceCommandInvoker>(
            new StaticPluginDeviceCommandInvoker(
                PluginDeviceCommandInvocationResult.Completed("{\"mode\":\"plugin\"}")));
        services.AddOpenLineOpsDevicesModule();

        using var serviceProvider = services.BuildServiceProvider();
        var executor = serviceProvider.GetRequiredService<IDeviceCommandExecutor>();
        var result = await executor.ExecuteAsync(CreatePluginRequest());

        Assert.Equal(DeviceCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"mode\":\"plugin\"}", result.ResultPayload);
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IPluginDeviceCommandInventory));
    }

    [Fact]
    public void AddOpenLineOpsDevicesModuleRegistersProjectReleaseRuntimeOrchestrator()
    {
        var services = new ServiceCollection();

        services.AddOpenLineOpsDevicesModule();

        var registration = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IRuntimeCommandExecutor));
        Assert.Equal("ProjectReleaseRuntimeCommandExecutor", registration.ImplementationType?.Name);
    }

    [Fact]
    public void AddOpenLineOpsDevicesModuleBindsPythonScriptWorkerSandboxOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Scripting:Python:ExecutionMode"] = "ProcessIsolated",
                ["OpenLineOps:Runtime:Scripting:Python:WorkerFileName"] = "dotnet",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:IsolationMode"] = "LeastPrivilegeIdentity",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:LeastPrivilegeIdentity"] = "openlineops-script",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:LeastPrivilegeLauncherExecutable"] = "sudo"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsDevicesModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<PythonScriptRuntimeOptions>();

        Assert.Equal("ProcessIsolated", options.ExecutionMode);
        Assert.Equal("dotnet", options.WorkerFileName);
        Assert.Equal(PythonScriptWorkerIsolationModes.LeastPrivilegeIdentity, options.Sandbox.IsolationMode);
        Assert.Equal("openlineops-script", options.Sandbox.LeastPrivilegeIdentity);
        Assert.Equal("sudo", options.Sandbox.LeastPrivilegeLauncherExecutable);
    }

    [Fact]
    public void AddOpenLineOpsDevicesModuleUsesOnlyProcessIsolatedPythonExecutionByDefault()
    {
        var services = new ServiceCollection();

        services.AddOpenLineOpsDevicesModule();

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<PythonScriptRuntimeOptions>();
        var executor = serviceProvider.GetRequiredService<IRuntimeScriptExecutor>();

        Assert.Equal(PythonScriptRuntimeExecutionModes.ProcessIsolated, options.ExecutionMode);
        Assert.IsType<ProcessIsolatedPythonScriptRuntimeScriptExecutor>(executor);
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IRuntimeScriptExecutor));
    }

    [Theory]
    [InlineData("InProcessTrusted")]
    [InlineData("Processisolated")]
    [InlineData("Worker")]
    [InlineData("")]
    public void AddOpenLineOpsDevicesModuleRejectsNonCanonicalPythonExecutionMode(string executionMode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Scripting:Python:ExecutionMode"] = executionMode
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenLineOpsDevicesModule(configuration));

        Assert.Contains("Expected exactly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddOpenLineOpsDevicesModuleCanSelectSqlitePersistence()
    {
        using var database = TemporarySqliteDatabase.Create();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Devices:Persistence:Provider"] = DevicePersistenceProviders.Sqlite,
                ["OpenLineOps:Devices:Persistence:ConnectionString"] = database.ConnectionString
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsDevicesModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        Assert.IsType<EfDeviceDefinitionRepository>(
            scope.ServiceProvider.GetRequiredService<IDeviceDefinitionRepository>());
        Assert.IsType<EfDeviceInstanceRepository>(
            scope.ServiceProvider.GetRequiredService<IDeviceInstanceRepository>());
    }

    [Fact]
    public void AddOpenLineOpsDevicesModuleUsesSqlitePersistenceByDefault()
    {
        var services = new ServiceCollection();
        services.AddOpenLineOpsDevicesModule();

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        Assert.IsType<EfDeviceDefinitionRepository>(
            scope.ServiceProvider.GetRequiredService<IDeviceDefinitionRepository>());
        Assert.IsType<EfDeviceInstanceRepository>(
            scope.ServiceProvider.GetRequiredService<IDeviceInstanceRepository>());
    }

    [Fact]
    public void AddOpenLineOpsDevicesModuleCanSelectInMemoryPersistence()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Devices:Persistence:Provider"] = DevicePersistenceProviders.InMemory
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsDevicesModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        Assert.IsType<InMemoryDeviceDefinitionRepository>(
            serviceProvider.GetRequiredService<IDeviceDefinitionRepository>());
        Assert.IsType<InMemoryDeviceInstanceRepository>(
            serviceProvider.GetRequiredService<IDeviceInstanceRepository>());
    }

    [Theory]
    [InlineData("EfSqlite")]
    [InlineData("EntityFrameworkSqlite")]
    [InlineData("sqlite")]
    [InlineData("Memory")]
    public void AddOpenLineOpsDevicesModuleRejectsNonCanonicalProviderTokens(string provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Devices:Persistence:Provider"] = provider
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddOpenLineOpsDevicesModule(configuration));

        Assert.Contains("Expected exactly 'Sqlite' or 'InMemory'", exception.Message, StringComparison.Ordinal);
    }

    private static DeviceCommandExecutionRequest CreateSimulatorRequest()
    {
        return new DeviceCommandExecutionRequest(
            ProjectReleaseRuntimeProviderKinds.Simulator,
            "simulator://scanner-01",
            new DeviceInstanceId("scanner-01"),
            new DeviceCommandDefinitionId("device.scanner:scan"),
            new DeviceCapabilityId("device.scanner"),
            "Scan",
            "{\"serial\":\"ABC\"}",
            TimeSpan.FromSeconds(30));
    }

    private static DeviceCommandExecutionRequest CreatePluginRequest()
    {
        return new DeviceCommandExecutionRequest(
            ProjectReleaseRuntimeProviderKinds.PluginCommand,
            "plugin://openlineops.scanner-driver/device.scanner:scan",
            new DeviceInstanceId("scanner-01"),
            new DeviceCommandDefinitionId("device.scanner:scan"),
            new DeviceCapabilityId("device.scanner"),
            "Scan",
            "{\"serial\":\"ABC\"}",
            TimeSpan.FromSeconds(30),
            new DevicePluginPackageIdentity(
                "openlineops.scanner-driver",
                "1.0.0",
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                "1.0.0",
                "win-x64",
                "openlineops.plugin-abi/1"));
    }

    private sealed class StaticPluginDeviceCommandInvoker(
        PluginDeviceCommandInvocationResult result) : IPluginDeviceCommandInvoker
    {
        public ValueTask<PluginDeviceCommandInvocationResult> ExecuteAsync(
            PluginDeviceCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            Directory = directory;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        private string Directory { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "devices-ef.sqlite");
            return new TemporarySqliteDatabase(directory, databasePath);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
