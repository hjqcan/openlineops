using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Infrastructure.Lifecycle;

namespace OpenLineOps.Plugins.Tests;

public sealed class ExternalPluginProcessStartInfoBuilderTests
{
    [Fact]
    public void BuildUsesExternalProcessStartInfoWhenIsolationModeIsExternalProcess()
    {
        var request = CreateStartRequest();
        var options = new ExternalProcessPluginHostOptions
        {
            ExecutablePath = "dotnet",
            ArgumentsTemplate = "\"{EntryAssemblyPath}\" --manifest \"{ManifestPath}\""
        };

        var startInfo = ExternalPluginProcessStartInfoBuilder.Build(options, request);

        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal(request.PackagePath, startInfo.WorkingDirectory);
        Assert.Contains(request.EntryAssemblyPath, startInfo.Arguments, StringComparison.Ordinal);
        Assert.Contains(request.ManifestPath, startInfo.Arguments, StringComparison.Ordinal);
        Assert.Equal(request.EntryAssemblyPath, startInfo.Environment["OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY"]);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public void BuildUsesContainerRuntimeAndMountsPluginPackageReadOnly()
    {
        var request = CreateStartRequest();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                IsolationMode = ExternalPluginIsolationModes.Container,
                ContainerRuntimeExecutable = "podman",
                ContainerImage = "openlineops/plugin-host:1.0.0",
                ContainerPackagePath = "/opt/openlineops/plugin",
                ContainerWorkingDirectory = "/opt/openlineops/plugin",
                ContainerExecutablePath = "dotnet",
                ContainerArgumentsTemplate = "\"{EntryAssemblyPath}\" --openlineops-plugin-host --manifest \"{ManifestPath}\" --type \"{EntryType}\"",
                LeastPrivilegeIdentity = "10001:10001",
                MaxCommandTimeout = TimeSpan.FromSeconds(7)
            }
        };
        options.Sandbox.AdditionalContainerRunArguments.Add("--pull=never");

        var startInfo = ExternalPluginProcessStartInfoBuilder.Build(options, request);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("podman", startInfo.FileName);
        Assert.Equal(request.PackagePath, startInfo.WorkingDirectory);
        Assert.Contains("run", arguments);
        Assert.Contains("--rm", arguments);
        Assert.Contains("--interactive", arguments);
        Assert.Contains("--network", arguments);
        Assert.Contains("none", arguments);
        Assert.Contains("--security-opt", arguments);
        Assert.Contains("no-new-privileges", arguments);
        Assert.Contains("--cap-drop", arguments);
        Assert.Contains("ALL", arguments);
        Assert.Contains("--user", arguments);
        Assert.Contains("10001:10001", arguments);
        Assert.Contains(
            $"type=bind,source={request.PackagePath},target=/opt/openlineops/plugin,readonly",
            arguments);
        Assert.Contains("--pull=never", arguments);
        Assert.Contains("openlineops/plugin-host:1.0.0", arguments);
        Assert.Contains("dotnet", arguments);
        Assert.Contains("/opt/openlineops/plugin/Plugin.dll", arguments);
        Assert.Contains("/opt/openlineops/plugin/manifest.json", arguments);
        Assert.Contains("External.Process.Test.Plugin", arguments);
        Assert.DoesNotContain(request.EntryAssemblyPath, arguments);
        Assert.Contains(
            "OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY=/opt/openlineops/plugin/Plugin.dll",
            arguments);
        Assert.Contains(
            "OPENLINEOPS_PLUGIN_MANIFEST_PATH=/opt/openlineops/plugin/manifest.json",
            arguments);
        Assert.Contains(
            "OPENLINEOPS_PLUGIN_SANDBOX_MAX_COMMAND_TIMEOUT_MS=7000",
            arguments);
    }

    [Fact]
    public void BuildUsesLeastPrivilegeLauncherForLeastPrivilegeIdentityIsolation()
    {
        var request = CreateStartRequest(ExternalPluginIsolationModes.LeastPrivilegeIdentity);
        var options = new ExternalProcessPluginHostOptions
        {
            ExecutablePath = "dotnet",
            ArgumentsTemplate = "\"{EntryAssemblyPath}\" --openlineops-plugin-host --manifest \"{ManifestPath}\"",
            Sandbox = new ExternalPluginSandboxOptions
            {
                IsolationMode = ExternalPluginIsolationModes.LeastPrivilegeIdentity,
                LeastPrivilegeIdentity = "openlineops-plugin",
                LeastPrivilegeLauncherExecutable = "sudo"
            }
        };

        var startInfo = ExternalPluginProcessStartInfoBuilder.Build(options, request);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("sudo", startInfo.FileName);
        Assert.Equal(request.PackagePath, startInfo.WorkingDirectory);
        Assert.Contains("-n", arguments);
        Assert.Contains("-u", arguments);
        Assert.Contains("openlineops-plugin", arguments);
        Assert.Contains("--", arguments);
        Assert.Contains("env", arguments);
        Assert.Contains($"OPENLINEOPS_PLUGIN_ID={request.Manifest.Id}", arguments);
        Assert.Contains($"OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY={request.EntryAssemblyPath}", arguments);
        Assert.Contains("dotnet", arguments);
        Assert.Contains(request.EntryAssemblyPath, arguments);
        Assert.Contains(request.ManifestPath, arguments);
        Assert.Equal(request.EntryAssemblyPath, startInfo.Environment["OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY"]);
    }

    [Fact]
    public void BuildSupportsCustomLeastPrivilegeLauncherTemplate()
    {
        var request = CreateStartRequest(ExternalPluginIsolationModes.LeastPrivilegeIdentity);
        var options = new ExternalProcessPluginHostOptions
        {
            ExecutablePath = "dotnet",
            ArgumentsTemplate = "\"{EntryAssemblyPath}\" --manifest \"{ManifestPath}\"",
            Sandbox = new ExternalPluginSandboxOptions
            {
                IsolationMode = ExternalPluginIsolationModes.LeastPrivilegeIdentity,
                LeastPrivilegeIdentity = "openlineops-plugin",
                LeastPrivilegeLauncherExecutable = "runuser",
                LeastPrivilegeArgumentsTemplate =
                    "-u {LeastPrivilegeIdentity} -- env {EnvironmentAssignments} {ExecutablePath} {Arguments}"
            }
        };

        var startInfo = ExternalPluginProcessStartInfoBuilder.Build(options, request);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("runuser", startInfo.FileName);
        Assert.Contains("-u", arguments);
        Assert.Contains("openlineops-plugin", arguments);
        Assert.Contains("env", arguments);
        Assert.Contains($"OPENLINEOPS_PLUGIN_PACKAGE_PATH={request.PackagePath}", arguments);
        Assert.Contains("dotnet", arguments);
        Assert.Contains(request.EntryAssemblyPath, arguments);
        Assert.Contains(request.ManifestPath, arguments);
    }

    [Fact]
    public void BuildUsesDockerForContainerIsolationByDefault()
    {
        var request = CreateStartRequest();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                IsolationMode = ExternalPluginIsolationModes.Container,
                ContainerImage = "openlineops/plugin-host:1.0.0"
            }
        };

        var startInfo = ExternalPluginProcessStartInfoBuilder.Build(options, request);

        Assert.Equal("docker", startInfo.FileName);
    }

    [Theory]
    [InlineData("Podman")]
    [InlineData("Docker")]
    [InlineData("container")]
    [InlineData("")]
    public void BuildRejectsNonCanonicalIsolationMode(string isolationMode)
    {
        var request = CreateStartRequest();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                IsolationMode = isolationMode,
                ContainerImage = "openlineops/plugin-host:1.0.0"
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ExternalPluginProcessStartInfoBuilder.Build(options, request));

        Assert.Contains("Expected exactly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectsLeastPrivilegeIdentityIsolationWithoutIdentity()
    {
        var request = CreateStartRequest(ExternalPluginIsolationModes.LeastPrivilegeIdentity);
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                IsolationMode = ExternalPluginIsolationModes.LeastPrivilegeIdentity,
                LeastPrivilegeLauncherExecutable = "sudo"
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ExternalPluginProcessStartInfoBuilder.Build(options, request));

        Assert.Contains("requires an identity", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectsContainerIsolationWithoutContainerImage()
    {
        var request = CreateStartRequest();
        var options = new ExternalProcessPluginHostOptions
        {
            Sandbox = new ExternalPluginSandboxOptions
            {
                IsolationMode = ExternalPluginIsolationModes.Container
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ExternalPluginProcessStartInfoBuilder.Build(options, request));

        Assert.Contains("requires a container image", exception.Message, StringComparison.Ordinal);
    }

    private static ExternalPluginProcessStartRequest CreateStartRequest(
        string isolationMode = ExternalPluginIsolationModes.Container)
    {
        var packagePath = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "openlineops-plugin-startinfo"));
        var manifestPath = Path.Combine(packagePath, "manifest.json");
        var entryAssemblyPath = Path.Combine(packagePath, "Plugin.dll");
        var manifest = new PluginManifest(
            "openlineops.external-process-test-plugin",
            "External Process Test Plugin",
            "1.0.0",
            PluginKind.DeviceDriver,
            "Plugin.dll",
            "External.Process.Test.Plugin",
            ["device.external-process"]);

        return new ExternalPluginProcessStartRequest(
            new PluginPackageExecutionIdentity(
                "project.test",
                "application.test",
                new PluginPackageRuntimeIdentity(
                    manifest.Id,
                    manifest.Version,
                    new string('a', 64),
                    new string('b', 64),
                    new string('c', 64),
                    "1.0.0",
                    "win-x64",
                    "openlineops.plugin-abi/1")),
            manifest,
            packagePath,
            manifestPath,
            entryAssemblyPath,
            manifest.EntryType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OPENLINEOPS_PLUGIN_ID"] = manifest.Id,
                ["OPENLINEOPS_PLUGIN_PACKAGE_PATH"] = packagePath,
                ["OPENLINEOPS_PLUGIN_MANIFEST_PATH"] = manifestPath,
                ["OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY"] = entryAssemblyPath,
                ["OPENLINEOPS_PLUGIN_ENTRY_TYPE"] = manifest.EntryType,
                ["OPENLINEOPS_PLUGIN_SANDBOX_ISOLATION_MODE"] = isolationMode,
                ["OPENLINEOPS_PLUGIN_SANDBOX_MAX_COMMAND_TIMEOUT_MS"] = "7000"
            });
    }
}
