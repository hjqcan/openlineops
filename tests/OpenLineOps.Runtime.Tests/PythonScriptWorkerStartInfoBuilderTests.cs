using OpenLineOps.Runtime.Infrastructure.Scripting;

namespace OpenLineOps.Runtime.Tests;

public sealed class PythonScriptWorkerStartInfoBuilderTests
{
    [Fact]
    public void BuildUsesExternalProcessStartInfoByDefault()
    {
        var options = new PythonScriptRuntimeOptions
        {
            WorkerFileName = "dotnet",
            WorkerArguments = "\"OpenLineOps.ScriptWorker.dll\"",
            WorkerWorkingDirectory = Path.GetTempPath()
        };

        var startInfo = PythonScriptWorkerStartInfoBuilder.Build(options);

        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal("\"OpenLineOps.ScriptWorker.dll\"", startInfo.Arguments);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), startInfo.WorkingDirectory);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(
            PythonScriptWorkerIsolationModes.ExternalProcess,
            startInfo.Environment["OPENLINEOPS_SCRIPT_WORKER_SANDBOX_ISOLATION_MODE"]);
    }

    [Fact]
    public void BuildUsesContainerRuntimeAndMountsWorkerWorkspace()
    {
        var workspacePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "openlineops-script-worker"));
        var workerPath = Path.Combine(workspacePath, "bin", "OpenLineOps.ScriptWorker.dll");
        var options = new PythonScriptRuntimeOptions
        {
            WorkerFileName = "dotnet",
            WorkerArguments = $"\"{workerPath}\"",
            Sandbox = new PythonScriptWorkerSandboxOptions
            {
                IsolationMode = PythonScriptWorkerIsolationModes.Container,
                ContainerRuntimeExecutable = "podman",
                ContainerImage = "openlineops/script-worker:1.0.0",
                ContainerMountSource = workspacePath,
                ContainerWorkspacePath = "/worker",
                ContainerWorkingDirectory = "/worker",
                LeastPrivilegeIdentity = "10001:10001"
            }
        };
        options.Sandbox.AdditionalContainerRunArguments.Add("--pull=never");

        var startInfo = PythonScriptWorkerStartInfoBuilder.Build(options);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("podman", startInfo.FileName);
        Assert.Equal(workspacePath, startInfo.WorkingDirectory);
        Assert.Contains("run", arguments);
        Assert.Contains("--rm", arguments);
        Assert.Contains("--interactive", arguments);
        Assert.Contains("--network", arguments);
        Assert.Contains("none", arguments);
        Assert.Contains("--security-opt", arguments);
        Assert.Contains("no-new-privileges", arguments);
        Assert.Contains("--cap-drop", arguments);
        Assert.Contains("ALL", arguments);
        Assert.Contains("--read-only", arguments);
        Assert.Contains("--pids-limit", arguments);
        Assert.Contains("128", arguments);
        Assert.Contains("--user", arguments);
        Assert.Contains("10001:10001", arguments);
        Assert.Contains($"type=bind,source={workspacePath},target=/worker,readonly", arguments);
        Assert.Contains("--pull=never", arguments);
        Assert.Contains("openlineops/script-worker:1.0.0", arguments);
        Assert.Contains("dotnet", arguments);
        Assert.Contains("/worker/bin/OpenLineOps.ScriptWorker.dll", arguments);
        Assert.DoesNotContain(workerPath, arguments);
        Assert.Contains(
            "OPENLINEOPS_SCRIPT_WORKER_SANDBOX_ISOLATION_MODE=Container",
            arguments);
    }

    [Fact]
    public void BuildUsesLeastPrivilegeLauncherForIdentityIsolation()
    {
        var options = new PythonScriptRuntimeOptions
        {
            WorkerFileName = "dotnet",
            WorkerArguments = "\"OpenLineOps.ScriptWorker.dll\"",
            Sandbox = new PythonScriptWorkerSandboxOptions
            {
                IsolationMode = PythonScriptWorkerIsolationModes.LeastPrivilegeIdentity,
                LeastPrivilegeIdentity = "openlineops-script",
                LeastPrivilegeLauncherExecutable = "sudo"
            }
        };

        var startInfo = PythonScriptWorkerStartInfoBuilder.Build(options);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("sudo", startInfo.FileName);
        Assert.Contains("-n", arguments);
        Assert.Contains("-u", arguments);
        Assert.Contains("openlineops-script", arguments);
        Assert.Contains("--", arguments);
        Assert.Contains("env", arguments);
        Assert.Contains("OPENLINEOPS_SCRIPT_WORKER_SANDBOX_ISOLATION_MODE=LeastPrivilegeIdentity", arguments);
        Assert.Contains("dotnet", arguments);
        Assert.Contains("OpenLineOps.ScriptWorker.dll", arguments);
    }

    [Fact]
    public void ValidateSandboxPolicyRejectsRequiredLeastPrivilegeWithoutIsolation()
    {
        var options = new PythonScriptRuntimeOptions
        {
            WorkerFileName = "dotnet",
            Sandbox = new PythonScriptWorkerSandboxOptions
            {
                RequireLeastPrivilegeExecution = true
            }
        };

        var error = PythonScriptWorkerStartInfoBuilder.ValidateSandboxPolicy(options);

        Assert.NotNull(error);
        Assert.Contains("least-privilege execution", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSandboxPolicyRejectsContainerIsolationWithoutImage()
    {
        var options = new PythonScriptRuntimeOptions
        {
            Sandbox = new PythonScriptWorkerSandboxOptions
            {
                IsolationMode = PythonScriptWorkerIsolationModes.Container
            }
        };

        var error = PythonScriptWorkerStartInfoBuilder.ValidateSandboxPolicy(options);

        Assert.NotNull(error);
        Assert.Contains("container image", error, StringComparison.Ordinal);
    }
}
