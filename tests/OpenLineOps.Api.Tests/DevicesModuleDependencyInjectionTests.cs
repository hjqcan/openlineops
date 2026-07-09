using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Devices.Api.DependencyInjection;
using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Persistence;
using OpenLineOps.Devices.Infrastructure.Persistence.Ef;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Infrastructure.Scripting;

namespace OpenLineOps.Api.Tests;

public sealed class DevicesModuleDependencyInjectionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AddOpenLineOpsDevicesModuleRegistersDefaultCommandExecutorWithoutConfiguration()
    {
        var services = new ServiceCollection();
        services.AddOpenLineOpsDevicesModule();

        using var serviceProvider = services.BuildServiceProvider();

        var executor = serviceProvider.GetRequiredService<IDeviceCommandExecutor>();

        var result = await executor.ExecuteAsync(CreateRequest());

        Assert.Equal(DeviceCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Contains("\"commandName\":\"Scan\"", result.ResultPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddOpenLineOpsDevicesModuleSelectsConfiguredSimulatorCommandExecutorFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Devices:CommandExecution:Provider"] = "ConfiguredSimulator",
                ["OpenLineOps:Devices:CommandExecution:ConfiguredSimulator:Commands:0:CommandDefinitionId"] = "device.scanner:scan",
                ["OpenLineOps:Devices:CommandExecution:ConfiguredSimulator:Commands:0:ResultPayload"] = "{\"mode\":\"configured\"}"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsDevicesModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var executor = serviceProvider.GetRequiredService<IDeviceCommandExecutor>();

        var result = await executor.ExecuteAsync(CreateRequest());

        Assert.Equal(DeviceCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"mode\":\"configured\"}", result.ResultPayload);
    }

    [Fact]
    public async Task AddOpenLineOpsDevicesModuleSelectsPluginCommandExecutorFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Devices:CommandExecution:Provider"] = "Plugin"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IPluginDeviceCommandInventory>(
            new InMemoryPluginDeviceCommandInventory(ScannerCommand));
        services.AddSingleton<IPluginDeviceCommandInvoker>(
            new StaticPluginDeviceCommandInvoker(
                PluginDeviceCommandInvocationResult.Completed("{\"mode\":\"plugin\"}")));
        services.AddOpenLineOpsDevicesModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var executor = serviceProvider.GetRequiredService<IDeviceCommandExecutor>();

        var result = await executor.ExecuteAsync(CreateRequest());

        Assert.Equal(DeviceCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"mode\":\"plugin\"}", result.ResultPayload);
    }

    [Fact]
    public async Task AddOpenLineOpsDevicesModulePreservesPluginRuntimeCommandExecutorFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:CommandExecutor"] = "Plugin"
            })
            .Build();
        var services = new ServiceCollection();
        var invoker = new StaticPluginProcessCommandInvoker(
            PluginProcessCommandInvocationResult.Completed("{\"mode\":\"process-plugin\"}"));
        services.AddSingleton<IPluginProcessCommandInventory>(
            new InMemoryPluginProcessCommandInventory(VisionCommand));
        services.AddSingleton<IPluginProcessCommandInvoker>(invoker);
        services.AddOpenLineOpsDevicesModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var executor = serviceProvider.GetRequiredService<IRuntimeCommandExecutor>();

        var result = await executor.ExecuteAsync(CreateRuntimeContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"mode\":\"process-plugin\"}", result.Payload);
    }

    [Fact]
    public async Task AddOpenLineOpsDevicesModuleRoutesPythonScriptCommandToScriptExecutor()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:CommandExecutor"] = "Device"
            })
            .Build();
        var services = new ServiceCollection();
        var scriptExecutor = new CapturingRuntimeScriptExecutor(
            RuntimeCommandExecutionResult.Completed("{\"mode\":\"script\"}"));
        services.AddSingleton<IRuntimeScriptExecutor>(scriptExecutor);
        services.AddOpenLineOpsDevicesModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var executor = serviceProvider.GetRequiredService<IRuntimeCommandExecutor>();

        var result = await executor.ExecuteAsync(CreateScriptRuntimeContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"mode\":\"script\"}", result.Payload);
        Assert.NotNull(scriptExecutor.Request);
        Assert.Equal("Python", scriptExecutor.Request.ScriptLanguage);
        Assert.Equal("scan-ok", scriptExecutor.Request.InputPayload);
    }

    [Fact]
    public async Task AddOpenLineOpsDevicesModuleCanSelectProcessIsolatedPythonScriptExecutionMode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:CommandExecutor"] = "Device",
                ["OpenLineOps:Runtime:Scripting:Python:ExecutionMode"] = "ProcessIsolated"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsDevicesModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var executor = serviceProvider.GetRequiredService<IRuntimeCommandExecutor>();

        var result = await executor.ExecuteAsync(CreateScriptRuntimeContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("WorkerFileName", result.Reason, StringComparison.Ordinal);
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
    public void AddOpenLineOpsDevicesModuleCanSelectEfSqlitePersistence()
    {
        using var database = TemporarySqliteDatabase.Create();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Devices:Persistence:Provider"] = DevicePersistenceProviders.EfSqlite,
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
    public void AddOpenLineOpsDevicesModuleUsesEfSqlitePersistenceByDefault()
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
    public void AddOpenLineOpsDevicesModuleCanStillSelectInMemoryPersistence()
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

    private static DeviceCommandExecutionRequest CreateRequest()
    {
        return new DeviceCommandExecutionRequest(
            new DeviceInstanceId("scanner-01"),
            new DeviceCommandDefinitionId("device.scanner:scan"),
            new DeviceCapabilityId("device.scanner"),
            "Scan",
            "{\"serial\":\"ABC\"}",
            TimeSpan.FromSeconds(30));
    }

    private static RuntimeCommandExecutionContext CreateRuntimeContext()
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new StationId("station-a"),
            new ConfigurationSnapshotId("snapshot-20260629-001"),
            new RuntimeStepId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new RuntimeCommandId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            new RuntimeNodeId("node-inspect"),
            new RuntimeCapabilityId("process.vision"),
            "Inspect",
            "{\"serial\":\"ABC\"}",
            TimeSpan.FromSeconds(30));
    }

    private static RuntimeCommandExecutionContext CreateScriptRuntimeContext()
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new StationId("station-a"),
            new ConfigurationSnapshotId("snapshot-20260629-001"),
            new RuntimeStepId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new RuntimeCommandId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            new RuntimeNodeId("node-normalize"),
            new RuntimeCapabilityId(RuntimeScriptCommand.PythonCapability),
            RuntimeScriptCommand.PythonCommandName,
            JsonSerializer.Serialize(
                new RuntimeScriptCommandPayload(
                    "Python",
                    "result = {'normalized': input_payload}",
                    "7",
                    "scan-ok"),
                JsonOptions),
            TimeSpan.FromSeconds(15));
    }

    private static PluginDeviceCommandDescriptor ScannerCommand { get; } = new(
        "openlineops.scanner-driver",
        "Scanner Driver",
        PluginKind.DeviceDriver,
        "device.scanner:scan",
        "device.scanner",
        "Scan",
        null,
        null,
        30000,
        0);

    private static PluginProcessCommandDescriptor VisionCommand { get; } = new(
        "openlineops.vision-process-plugin",
        "Vision Process Plugin",
        PluginKind.ProcessNode,
        "process.vision:inspect",
        "process.vision",
        "Inspect",
        null,
        null,
        30000,
        0);

    private sealed class InMemoryPluginDeviceCommandInventory(
        params PluginDeviceCommandDescriptor[] commands) : IPluginDeviceCommandInventory
    {
        public ValueTask<IReadOnlyCollection<PluginDeviceCommandDescriptor>> ListDeviceCommandsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult<IReadOnlyCollection<PluginDeviceCommandDescriptor>>(commands);
        }
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

    private sealed class InMemoryPluginProcessCommandInventory(
        params PluginProcessCommandDescriptor[] commands) : IPluginProcessCommandInventory
    {
        public ValueTask<IReadOnlyCollection<PluginProcessCommandDescriptor>> ListProcessCommandsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult<IReadOnlyCollection<PluginProcessCommandDescriptor>>(commands);
        }
    }

    private sealed class StaticPluginProcessCommandInvoker(
        PluginProcessCommandInvocationResult result) : IPluginProcessCommandInvoker
    {
        public ValueTask<PluginProcessCommandInvocationResult> ExecuteAsync(
            PluginProcessCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CapturingRuntimeScriptExecutor(
        RuntimeCommandExecutionResult result) : IRuntimeScriptExecutor
    {
        public RuntimeScriptExecutionRequest? Request { get; private set; }

        public ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
            RuntimeScriptExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;

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
