using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

internal static class ExternalPluginProcessStartInfoBuilder
{
    public static ProcessStartInfo Build(
        ExternalProcessPluginHostOptions options,
        ExternalPluginProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        if (ExternalPluginIsolationModes.IsContainer(options.Sandbox.IsolationMode))
        {
            return BuildContainerStartInfo(options, request);
        }

        return ExternalPluginIsolationModes.IsLeastPrivilegeIdentity(options.Sandbox.IsolationMode)
            ? BuildLeastPrivilegeStartInfo(options, request)
            : BuildExternalProcessStartInfo(options, request);
    }

    private static ProcessStartInfo BuildExternalProcessStartInfo(
        ExternalProcessPluginHostOptions options,
        ExternalPluginProcessStartRequest request)
    {
        var startInfo = CreateBaseStartInfo(request.PackagePath);
        startInfo.FileName = string.IsNullOrWhiteSpace(options.ExecutablePath)
            ? "dotnet"
            : options.ExecutablePath.Trim();
        startInfo.Arguments = FormatArguments(options.ArgumentsTemplate, request, options.Sandbox);

        foreach (var environmentVariable in request.EnvironmentVariables)
        {
            startInfo.Environment[environmentVariable.Key] = environmentVariable.Value;
        }

        return startInfo;
    }

    private static ProcessStartInfo BuildLeastPrivilegeStartInfo(
        ExternalProcessPluginHostOptions options,
        ExternalPluginProcessStartRequest request)
    {
        var sandbox = options.Sandbox;
        if (string.IsNullOrWhiteSpace(sandbox.LeastPrivilegeIdentity))
        {
            throw new InvalidOperationException(
                $"Least-privilege identity isolation for plugin '{request.Manifest.Id}' requires an identity.");
        }

        if (OperatingSystem.IsWindows()
            && string.IsNullOrWhiteSpace(sandbox.LeastPrivilegeLauncherExecutable))
        {
            throw new InvalidOperationException(
                "Windows least-privilege plugin launch requires an explicit launcher executable because interactive runas cannot preserve the plugin JSON-lines protocol.");
        }

        var childExecutable = string.IsNullOrWhiteSpace(options.ExecutablePath)
            ? "dotnet"
            : options.ExecutablePath.Trim();
        var childArguments = FormatArguments(options.ArgumentsTemplate, request, sandbox);
        var startInfo = CreateBaseStartInfo(request.PackagePath);
        startInfo.FileName = ResolveLeastPrivilegeLauncherExecutable(sandbox);

        foreach (var environmentVariable in request.EnvironmentVariables)
        {
            startInfo.Environment[environmentVariable.Key] = environmentVariable.Value;
        }

        AddLeastPrivilegeArguments(
            startInfo.ArgumentList,
            sandbox,
            request,
            childExecutable,
            childArguments);

        return startInfo;
    }

    private static ProcessStartInfo BuildContainerStartInfo(
        ExternalProcessPluginHostOptions options,
        ExternalPluginProcessStartRequest request)
    {
        var sandbox = options.Sandbox;
        if (string.IsNullOrWhiteSpace(sandbox.ContainerImage))
        {
            throw new InvalidOperationException(
                $"Container isolation for plugin '{request.Manifest.Id}' requires a container image.");
        }

        var containerPackagePath = NormalizeContainerPath(sandbox.ContainerPackagePath, "/openlineops/plugin");
        var containerRequest = request with
        {
            PackagePath = containerPackagePath,
            ManifestPath = MapPackageRelativePath(request.PackagePath, request.ManifestPath, containerPackagePath),
            EntryAssemblyPath = MapPackageRelativePath(request.PackagePath, request.EntryAssemblyPath, containerPackagePath),
            EnvironmentVariables = BuildContainerEnvironmentVariables(request, containerPackagePath)
        };

        var startInfo = CreateBaseStartInfo(request.PackagePath);
        startInfo.FileName = ResolveContainerRuntimeExecutable(sandbox);

        AddContainerRunArguments(startInfo.ArgumentList, sandbox, request, containerRequest);

        return startInfo;
    }

    private static ProcessStartInfo CreateBaseStartInfo(string workingDirectory)
    {
        return new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
    }

    private static void AddContainerRunArguments(
        Collection<string> arguments,
        ExternalPluginSandboxOptions sandbox,
        ExternalPluginProcessStartRequest hostRequest,
        ExternalPluginProcessStartRequest containerRequest)
    {
        arguments.Add("run");
        arguments.Add("--rm");
        arguments.Add("--interactive");

        if (!string.IsNullOrWhiteSpace(sandbox.ContainerNetwork))
        {
            arguments.Add("--network");
            arguments.Add(sandbox.ContainerNetwork.Trim());
        }

        if (sandbox.ContainerNoNewPrivileges)
        {
            arguments.Add("--security-opt");
            arguments.Add("no-new-privileges");
        }

        if (sandbox.ContainerDropAllCapabilities)
        {
            arguments.Add("--cap-drop");
            arguments.Add("ALL");
        }

        if (sandbox.ContainerReadOnlyRootFilesystem)
        {
            arguments.Add("--read-only");
        }

        if (sandbox.ContainerPidsLimit > 0)
        {
            arguments.Add("--pids-limit");
            arguments.Add(sandbox.ContainerPidsLimit.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(sandbox.LeastPrivilegeIdentity))
        {
            arguments.Add("--user");
            arguments.Add(sandbox.LeastPrivilegeIdentity.Trim());
        }

        foreach (var environmentVariable in containerRequest.EnvironmentVariables)
        {
            arguments.Add("--env");
            arguments.Add($"{environmentVariable.Key}={environmentVariable.Value}");
        }

        arguments.Add("--mount");
        arguments.Add(
            $"type=bind,source={hostRequest.PackagePath},target={containerRequest.PackagePath},readonly");

        var workingDirectory = NormalizeContainerPath(
            sandbox.ContainerWorkingDirectory,
            containerRequest.PackagePath);
        arguments.Add("--workdir");
        arguments.Add(workingDirectory);

        foreach (var additionalArgument in sandbox.AdditionalContainerRunArguments)
        {
            if (!string.IsNullOrWhiteSpace(additionalArgument))
            {
                arguments.Add(additionalArgument.Trim());
            }
        }

        arguments.Add(sandbox.ContainerImage!.Trim());

        var executable = string.IsNullOrWhiteSpace(sandbox.ContainerExecutablePath)
            ? "dotnet"
            : sandbox.ContainerExecutablePath.Trim();
        arguments.Add(executable);

        var commandArguments = FormatArguments(
            sandbox.ContainerArgumentsTemplate,
            containerRequest,
            sandbox);
        foreach (var argument in SplitCommandLine(commandArguments))
        {
            arguments.Add(argument);
        }
    }

    private static void AddLeastPrivilegeArguments(
        Collection<string> arguments,
        ExternalPluginSandboxOptions sandbox,
        ExternalPluginProcessStartRequest request,
        string childExecutable,
        string childArguments)
    {
        if (!string.IsNullOrWhiteSpace(sandbox.LeastPrivilegeArgumentsTemplate))
        {
            var launcherArguments = FormatLeastPrivilegeArguments(
                sandbox.LeastPrivilegeArgumentsTemplate,
                request,
                sandbox,
                childExecutable,
                childArguments,
                FormatEnvironmentAssignments(request.EnvironmentVariables));
            foreach (var argument in SplitCommandLine(launcherArguments))
            {
                arguments.Add(argument);
            }

            return;
        }

        if (sandbox.LeastPrivilegeNoInteractivePrompt)
        {
            arguments.Add("-n");
        }

        arguments.Add("-u");
        arguments.Add(sandbox.LeastPrivilegeIdentity!.Trim());
        arguments.Add("--");
        arguments.Add("env");

        foreach (var environmentVariable in request.EnvironmentVariables)
        {
            arguments.Add($"{environmentVariable.Key}={environmentVariable.Value}");
        }

        arguments.Add(childExecutable);
        foreach (var childArgument in SplitCommandLine(childArguments))
        {
            arguments.Add(childArgument);
        }
    }

    private static Dictionary<string, string> BuildContainerEnvironmentVariables(
        ExternalPluginProcessStartRequest request,
        string containerPackagePath)
    {
        var containerManifestPath = MapPackageRelativePath(
            request.PackagePath,
            request.ManifestPath,
            containerPackagePath);
        var containerEntryAssemblyPath = MapPackageRelativePath(
            request.PackagePath,
            request.EntryAssemblyPath,
            containerPackagePath);
        var environmentVariables = new Dictionary<string, string>(request.EnvironmentVariables, StringComparer.Ordinal)
        {
            ["OPENLINEOPS_PLUGIN_PACKAGE_PATH"] = containerPackagePath,
            ["OPENLINEOPS_PLUGIN_MANIFEST_PATH"] = containerManifestPath,
            ["OPENLINEOPS_PLUGIN_ENTRY_ASSEMBLY"] = containerEntryAssemblyPath
        };

        return environmentVariables;
    }

    private static string ResolveContainerRuntimeExecutable(ExternalPluginSandboxOptions sandbox)
    {
        if (!string.IsNullOrWhiteSpace(sandbox.ContainerRuntimeExecutable))
        {
            return sandbox.ContainerRuntimeExecutable.Trim();
        }

        return string.Equals(sandbox.IsolationMode, "Podman", StringComparison.OrdinalIgnoreCase)
            ? "podman"
            : "docker";
    }

    private static string ResolveLeastPrivilegeLauncherExecutable(ExternalPluginSandboxOptions sandbox)
    {
        if (!string.IsNullOrWhiteSpace(sandbox.LeastPrivilegeLauncherExecutable))
        {
            return sandbox.LeastPrivilegeLauncherExecutable.Trim();
        }

        return "sudo";
    }

    private static string MapPackageRelativePath(
        string hostPackagePath,
        string hostPath,
        string containerPackagePath)
    {
        var relativePath = Path.GetRelativePath(hostPackagePath, hostPath);
        if (relativePath == ".")
        {
            return containerPackagePath;
        }

        return CombineContainerPath(
            containerPackagePath,
            relativePath.Replace('\\', '/'));
    }

    private static string NormalizeContainerPath(string? value, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();

        return path.Replace('\\', '/');
    }

    private static string CombineContainerPath(string root, string relativePath)
    {
        return $"{root.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }

    private static string FormatArguments(
        string template,
        ExternalPluginProcessStartRequest request,
        ExternalPluginSandboxOptions sandbox)
    {
        var argumentsTemplate = string.IsNullOrWhiteSpace(template)
            ? "\"{EntryAssemblyPath}\" --openlineops-plugin-host --manifest \"{ManifestPath}\""
            : template;

        return argumentsTemplate
            .Replace("{EntryAssemblyPath}", EscapeArgumentValue(request.EntryAssemblyPath), StringComparison.Ordinal)
            .Replace("{ManifestPath}", EscapeArgumentValue(request.ManifestPath), StringComparison.Ordinal)
            .Replace("{PackagePath}", EscapeArgumentValue(request.PackagePath), StringComparison.Ordinal)
            .Replace("{PluginId}", EscapeArgumentValue(request.Manifest.Id), StringComparison.Ordinal)
            .Replace("{EntryType}", EscapeArgumentValue(request.EntryType), StringComparison.Ordinal)
            .Replace("{IsolationMode}", EscapeArgumentValue(sandbox.IsolationMode), StringComparison.Ordinal)
            .Replace("{LeastPrivilegeIdentity}", EscapeArgumentValue(sandbox.LeastPrivilegeIdentity ?? string.Empty), StringComparison.Ordinal)
            .Replace("{ContainerImage}", EscapeArgumentValue(sandbox.ContainerImage ?? string.Empty), StringComparison.Ordinal)
            .Replace(
                "{MaxCommandTimeoutMilliseconds}",
                ToTimeoutMilliseconds(sandbox.MaxCommandTimeout).ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static string FormatLeastPrivilegeArguments(
        string template,
        ExternalPluginProcessStartRequest request,
        ExternalPluginSandboxOptions sandbox,
        string childExecutable,
        string childArguments,
        string environmentAssignments)
    {
        return FormatArguments(template, request, sandbox)
            .Replace("{ExecutablePath}", EscapeArgumentValue(childExecutable), StringComparison.Ordinal)
            .Replace("{Arguments}", childArguments, StringComparison.Ordinal)
            .Replace("{EnvironmentAssignments}", environmentAssignments, StringComparison.Ordinal);
    }

    private static string FormatEnvironmentAssignments(IReadOnlyDictionary<string, string> environmentVariables)
    {
        return string.Join(
            " ",
            environmentVariables.Select(environmentVariable =>
                $"\"{EscapeArgumentValue(environmentVariable.Key)}={EscapeArgumentValue(environmentVariable.Value)}\""));
    }

    private static List<string> SplitCommandLine(string commandLine)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (character == '"')
            {
                inQuotes = !inQuotes;

                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                AddCurrent();

                continue;
            }

            current.Append(character);
        }

        if (inQuotes)
        {
            throw new InvalidOperationException("Container command arguments contain an unterminated quote.");
        }

        AddCurrent();

        return arguments;

        void AddCurrent()
        {
            if (current.Length == 0)
            {
                return;
            }

            arguments.Add(current.ToString());
            current.Clear();
        }
    }

    private static string EscapeArgumentValue(string value)
    {
        return value.Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return 0;
        }

        if (timeout.TotalMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
    }
}
