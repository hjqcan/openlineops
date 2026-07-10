using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Infrastructure.Scripting;

namespace OpenLineOps.Api.Tests;

public sealed class RuntimeModuleDependencyInjectionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AddOpenLineOpsRuntimeModuleSelectsPluginCommandExecutorFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:CommandExecutor"] = "Plugin"
            })
            .Build();
        var services = new ServiceCollection();
        var invoker = new CapturingPluginProcessCommandInvoker(
            PluginProcessCommandInvocationResult.Completed("{\"mode\":\"process-plugin\"}"));
        services.AddSingleton<IPluginProcessCommandInventory>(
            new InMemoryPluginProcessCommandInventory(VisionCommand));
        services.AddSingleton<IPluginProcessCommandInvoker>(invoker);
        services.AddOpenLineOpsRuntimeModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var executor = serviceProvider.GetRequiredService<IRuntimeCommandExecutor>();

        var result = await executor.ExecuteAsync(CreateContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"mode\":\"process-plugin\"}", result.Payload);
        Assert.NotNull(invoker.Request);
        Assert.Equal("openlineops.vision-process-plugin", invoker.Request.PluginId);
        Assert.Equal("process.vision:inspect", invoker.Request.CommandDefinitionId);
    }

    [Fact]
    public async Task AddOpenLineOpsRuntimeModuleRoutesPythonScriptCommandToScriptExecutor()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:CommandExecutor"] = "Plugin"
            })
            .Build();
        var services = new ServiceCollection();
        const string scriptPayload =
            "{\"automation_plan\":[{\"type\":\"flow.wait\",\"duration_ms\":0}]}";
        var scriptExecutor = new CapturingRuntimeScriptExecutor(
            RuntimeCommandExecutionResult.Completed(scriptPayload));
        services.AddSingleton<IRuntimeScriptExecutor>(scriptExecutor);
        services.AddOpenLineOpsRuntimeModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var executor = serviceProvider.GetRequiredService<IRuntimeCommandExecutor>();

        var result = await executor.ExecuteAsync(CreateScriptContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(scriptPayload, result.Payload);
        Assert.NotNull(scriptExecutor.Request);
        Assert.Equal("Python", scriptExecutor.Request.ScriptLanguage);
        Assert.Equal("scan-ok", scriptExecutor.Request.InputPayload);
    }

    [Fact]
    public async Task AddOpenLineOpsRuntimeModuleRoutesFlowWaitBeforeConfiguredPluginExecutor()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:CommandExecutor"] = "Plugin"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsRuntimeModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var executor = serviceProvider.GetRequiredService<IRuntimeCommandExecutor>();
        var result = await executor.ExecuteAsync(CreateFlowWaitContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"durationMilliseconds\":0}", result.Payload);
    }

    [Fact]
    public async Task AddOpenLineOpsRuntimeModuleCanSelectProcessIsolatedPythonScriptExecutionMode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Scripting:Python:ExecutionMode"] = "ProcessIsolated"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsRuntimeModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var executor = serviceProvider.GetRequiredService<IRuntimeCommandExecutor>();

        var result = await executor.ExecuteAsync(CreateScriptContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("WorkerFileName", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AddOpenLineOpsRuntimeModuleBindsPythonScriptWorkerSandboxOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:Scripting:Python:ExecutionMode"] = "ProcessIsolated",
                ["OpenLineOps:Runtime:Scripting:Python:WorkerFileName"] = "dotnet",
                ["OpenLineOps:Runtime:Scripting:Python:WorkerArguments"] = "\"OpenLineOps.ScriptWorker.dll\"",
                ["OpenLineOps:Runtime:Scripting:Python:WorkerWorkingDirectory"] = "workers",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:RequireLeastPrivilegeExecution"] = "true",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:IsolationMode"] = "Container",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:ContainerRuntimeExecutable"] = "podman",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:ContainerImage"] = "openlineops/script-worker:1.0.0",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:ContainerWorkspacePath"] = "/worker",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:LeastPrivilegeIdentity"] = "10001:10001",
                ["OpenLineOps:Runtime:Scripting:Python:Sandbox:AdditionalContainerRunArguments:0"] = "--pull=never"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsRuntimeModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<PythonScriptRuntimeOptions>();

        Assert.Equal("ProcessIsolated", options.ExecutionMode);
        Assert.Equal("dotnet", options.WorkerFileName);
        Assert.Equal("\"OpenLineOps.ScriptWorker.dll\"", options.WorkerArguments);
        Assert.Equal("workers", options.WorkerWorkingDirectory);
        Assert.True(options.Sandbox.RequireLeastPrivilegeExecution);
        Assert.Equal(PythonScriptWorkerIsolationModes.Container, options.Sandbox.IsolationMode);
        Assert.Equal("podman", options.Sandbox.ContainerRuntimeExecutable);
        Assert.Equal("openlineops/script-worker:1.0.0", options.Sandbox.ContainerImage);
        Assert.Equal("/worker", options.Sandbox.ContainerWorkspacePath);
        Assert.Equal("10001:10001", options.Sandbox.LeastPrivilegeIdentity);
        Assert.Contains("--pull=never", options.Sandbox.AdditionalContainerRunArguments);
    }

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

    private static RuntimeCommandExecutionContext CreateContext()
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

    private static RuntimeCommandExecutionContext CreateScriptContext()
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

    private static RuntimeCommandExecutionContext CreateFlowWaitContext()
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new StationId("station-a"),
            new ConfigurationSnapshotId("snapshot-20260629-001"),
            new RuntimeStepId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new RuntimeCommandId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            new RuntimeNodeId("node-wait"),
            new RuntimeCapabilityId(RuntimeFlowCommand.Capability),
            RuntimeFlowCommand.WaitCommandName,
            "{\"durationMilliseconds\":0}",
            TimeSpan.FromSeconds(15));
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

    private sealed class CapturingPluginProcessCommandInvoker(
        PluginProcessCommandInvocationResult result) : IPluginProcessCommandInvoker
    {
        public PluginProcessCommandInvocationRequest? Request { get; private set; }

        public ValueTask<PluginProcessCommandInvocationResult> ExecuteAsync(
            PluginProcessCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;

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
}
