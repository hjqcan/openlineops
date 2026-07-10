using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Runtime.Api.DependencyInjection;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Infrastructure.Scripting;

namespace OpenLineOps.Api.Tests;

public sealed class RuntimeModuleDependencyInjectionTests
{
    [Fact]
    public void AddOpenLineOpsRuntimeModuleDoesNotRegisterAConfigurableCommandExecutor()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:CommandExecutor"] = "Plugin"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddOpenLineOpsRuntimeModule(configuration);

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IRuntimeCommandExecutor));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.Name.Contains("ConfigurableRuntimeCommandExecutor", StringComparison.Ordinal));
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
}
