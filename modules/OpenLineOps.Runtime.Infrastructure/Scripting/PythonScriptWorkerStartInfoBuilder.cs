using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;

namespace OpenLineOps.Runtime.Infrastructure.Scripting;

internal static class PythonScriptWorkerStartInfoBuilder
{
    public static ProcessStartInfo Build(PythonScriptRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return PythonScriptWorkerIsolationModes.Parse(options.Sandbox.IsolationMode) switch
        {
            PythonScriptWorkerIsolationMode.ExternalProcess => BuildExternalProcessStartInfo(options),
            PythonScriptWorkerIsolationMode.LeastPrivilegeIdentity => BuildLeastPrivilegeStartInfo(options),
            PythonScriptWorkerIsolationMode.Container => BuildContainerStartInfo(options),
            _ => throw new InvalidOperationException(
                $"Unsupported Python script worker isolation mode '{options.Sandbox.IsolationMode}'.")
        };
    }

    public static string? ValidateSandboxPolicy(PythonScriptRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sandbox = options.Sandbox;
        if (sandbox.RequireLeastPrivilegeExecution && !sandbox.HasLeastPrivilegeExecution)
        {
            return "Python script worker sandbox requires least-privilege execution, but no least-privilege identity or container isolation is configured.";
        }

        if (PythonScriptWorkerIsolationModes.IsLeastPrivilegeIdentity(sandbox.IsolationMode)
            && string.IsNullOrWhiteSpace(sandbox.LeastPrivilegeIdentity))
        {
            return "Least-privilege Python script worker isolation requires an identity.";
        }

        if (PythonScriptWorkerIsolationModes.IsLeastPrivilegeIdentity(sandbox.IsolationMode)
            && OperatingSystem.IsWindows()
            && string.IsNullOrWhiteSpace(sandbox.LeastPrivilegeLauncherExecutable))
        {
            return "Windows least-privilege Python script worker isolation requires an explicit launcher executable.";
        }

        if (PythonScriptWorkerIsolationModes.IsContainer(sandbox.IsolationMode)
            && string.IsNullOrWhiteSpace(sandbox.ContainerImage))
        {
            return "Container Python script worker isolation requires a container image.";
        }

        if (!PythonScriptWorkerIsolationModes.IsContainer(sandbox.IsolationMode)
            && string.IsNullOrWhiteSpace(options.WorkerFileName))
        {
            return $"Process-isolated Python script execution requires {PythonScriptRuntimeOptions.SectionName}:WorkerFileName.";
        }

        return null;
    }

    private static ProcessStartInfo BuildExternalProcessStartInfo(PythonScriptRuntimeOptions options)
    {
        var startInfo = CreateBaseStartInfo(options.WorkerWorkingDirectory);
        startInfo.FileName = options.WorkerFileName!.Trim();
        startInfo.Arguments = options.WorkerArguments ?? string.Empty;
        AddSandboxEnvironment(startInfo.Environment, options);

        return startInfo;
    }

    private static ProcessStartInfo BuildLeastPrivilegeStartInfo(PythonScriptRuntimeOptions options)
    {
        var sandbox = options.Sandbox;
        var childExecutable = options.WorkerFileName!.Trim();
        var childArguments = options.WorkerArguments ?? string.Empty;
        var startInfo = CreateBaseStartInfo(options.WorkerWorkingDirectory);
        startInfo.FileName = ResolveLeastPrivilegeLauncherExecutable(sandbox);
        AddSandboxEnvironment(startInfo.Environment, options);

        AddLeastPrivilegeArguments(
            startInfo.ArgumentList,
            sandbox,
            childExecutable,
            childArguments,
            FormatEnvironmentAssignments(BuildSandboxEnvironment(options)));

        return startInfo;
    }

    private static ProcessStartInfo BuildContainerStartInfo(PythonScriptRuntimeOptions options)
    {
        var sandbox = options.Sandbox;
        var hostMountSource = ResolveContainerMountSource(options);
        var containerWorkspacePath = NormalizeContainerPath(
            sandbox.ContainerWorkspacePath,
            "/openlineops/script-worker");
        var startInfo = CreateBaseStartInfo(hostMountSource);
        startInfo.FileName = ResolveContainerRuntimeExecutable(sandbox);

        AddContainerRunArguments(
            startInfo.ArgumentList,
            options,
            hostMountSource,
            containerWorkspacePath);

        return startInfo;
    }

    private static ProcessStartInfo CreateBaseStartInfo(string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = Path.GetFullPath(workingDirectory);
        }

        return startInfo;
    }

    private static void AddContainerRunArguments(
        Collection<string> arguments,
        PythonScriptRuntimeOptions options,
        string hostMountSource,
        string containerWorkspacePath)
    {
        var sandbox = options.Sandbox;
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

        foreach (var environmentVariable in BuildSandboxEnvironment(options))
        {
            arguments.Add("--env");
            arguments.Add($"{environmentVariable.Key}={environmentVariable.Value}");
        }

        arguments.Add("--mount");
        arguments.Add(
            "type=bind,source="
            + hostMountSource
            + ",target="
            + containerWorkspacePath
            + (sandbox.ContainerMountReadOnly ? ",readonly" : string.Empty));

        var workingDirectory = NormalizeContainerPath(
            sandbox.ContainerWorkingDirectory,
            containerWorkspacePath);
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
            ? MapHostPathIfUnderMount(options.WorkerFileName ?? "dotnet", hostMountSource, containerWorkspacePath)
            : sandbox.ContainerExecutablePath.Trim();
        arguments.Add(executable);

        var workerArguments = MapHostPathText(
            options.WorkerArguments ?? string.Empty,
            hostMountSource,
            containerWorkspacePath);
        var commandArguments = FormatContainerArguments(
            sandbox.ContainerArgumentsTemplate,
            options,
            hostMountSource,
            containerWorkspacePath,
            workerArguments);
        foreach (var argument in SplitCommandLine(commandArguments))
        {
            arguments.Add(argument);
        }
    }

    private static void AddLeastPrivilegeArguments(
        Collection<string> arguments,
        PythonScriptWorkerSandboxOptions sandbox,
        string childExecutable,
        string childArguments,
        string environmentAssignments)
    {
        if (!string.IsNullOrWhiteSpace(sandbox.LeastPrivilegeArgumentsTemplate))
        {
            var launcherArguments = sandbox.LeastPrivilegeArgumentsTemplate
                .Replace("{LeastPrivilegeIdentity}", EscapeArgumentValue(sandbox.LeastPrivilegeIdentity ?? string.Empty), StringComparison.Ordinal)
                .Replace("{ExecutablePath}", EscapeArgumentValue(childExecutable), StringComparison.Ordinal)
                .Replace("{Arguments}", childArguments, StringComparison.Ordinal)
                .Replace("{EnvironmentAssignments}", environmentAssignments, StringComparison.Ordinal)
                .Replace("{IsolationMode}", EscapeArgumentValue(sandbox.IsolationMode), StringComparison.Ordinal);
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

        foreach (var environmentAssignment in SplitCommandLine(environmentAssignments))
        {
            arguments.Add(environmentAssignment);
        }

        arguments.Add(childExecutable);
        foreach (var childArgument in SplitCommandLine(childArguments))
        {
            arguments.Add(childArgument);
        }
    }

    private static string FormatContainerArguments(
        string template,
        PythonScriptRuntimeOptions options,
        string hostMountSource,
        string containerWorkspacePath,
        string workerArguments)
    {
        var workerFileName = MapHostPathIfUnderMount(
            options.WorkerFileName ?? string.Empty,
            hostMountSource,
            containerWorkspacePath);

        return (string.IsNullOrWhiteSpace(template) ? "{WorkerArguments}" : template)
            .Replace("{WorkerFileName}", EscapeArgumentValue(workerFileName), StringComparison.Ordinal)
            .Replace("{WorkerArguments}", workerArguments, StringComparison.Ordinal)
            .Replace("{WorkerWorkingDirectory}", EscapeArgumentValue(options.WorkerWorkingDirectory ?? string.Empty), StringComparison.Ordinal)
            .Replace("{ContainerWorkspacePath}", EscapeArgumentValue(containerWorkspacePath), StringComparison.Ordinal)
            .Replace("{ContainerImage}", EscapeArgumentValue(options.Sandbox.ContainerImage ?? string.Empty), StringComparison.Ordinal)
            .Replace("{IsolationMode}", EscapeArgumentValue(options.Sandbox.IsolationMode), StringComparison.Ordinal)
            .Replace("{LeastPrivilegeIdentity}", EscapeArgumentValue(options.Sandbox.LeastPrivilegeIdentity ?? string.Empty), StringComparison.Ordinal);
    }

    private static Dictionary<string, string> BuildSandboxEnvironment(PythonScriptRuntimeOptions options)
    {
        var sandbox = options.Sandbox;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENLINEOPS_SCRIPT_WORKER_SANDBOX_ISOLATION_MODE"] = sandbox.IsolationMode,
            ["OPENLINEOPS_SCRIPT_WORKER_SANDBOX_REQUIRE_LEAST_PRIVILEGE"] = sandbox.RequireLeastPrivilegeExecution.ToString(CultureInfo.InvariantCulture),
            ["OPENLINEOPS_SCRIPT_WORKER_SANDBOX_IDENTITY"] = sandbox.LeastPrivilegeIdentity ?? string.Empty
        };
    }

    private static void AddSandboxEnvironment(
        IDictionary<string, string?> environment,
        PythonScriptRuntimeOptions options)
    {
        foreach (var environmentVariable in BuildSandboxEnvironment(options))
        {
            environment[environmentVariable.Key] = environmentVariable.Value;
        }
    }

    private static string FormatEnvironmentAssignments(IReadOnlyDictionary<string, string> environmentVariables)
    {
        return string.Join(
            " ",
            environmentVariables.Select(environmentVariable =>
                $"\"{EscapeArgumentValue(environmentVariable.Key)}={EscapeArgumentValue(environmentVariable.Value)}\""));
    }

    private static string ResolveContainerMountSource(PythonScriptRuntimeOptions options)
    {
        var source = options.Sandbox.ContainerMountSource;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = !string.IsNullOrWhiteSpace(options.WorkerWorkingDirectory)
                ? options.WorkerWorkingDirectory
                : Directory.GetCurrentDirectory();
        }

        return Path.GetFullPath(source);
    }

    private static string ResolveContainerRuntimeExecutable(PythonScriptWorkerSandboxOptions sandbox)
    {
        if (!string.IsNullOrWhiteSpace(sandbox.ContainerRuntimeExecutable))
        {
            return sandbox.ContainerRuntimeExecutable.Trim();
        }

        return "docker";
    }

    private static string ResolveLeastPrivilegeLauncherExecutable(PythonScriptWorkerSandboxOptions sandbox)
    {
        if (!string.IsNullOrWhiteSpace(sandbox.LeastPrivilegeLauncherExecutable))
        {
            return sandbox.LeastPrivilegeLauncherExecutable.Trim();
        }

        return "sudo";
    }

    private static string NormalizeContainerPath(string? value, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();

        return path.Replace('\\', '/');
    }

    private static string MapHostPathText(
        string text,
        string hostMountSource,
        string containerWorkspacePath)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var hostRoot = Path.GetFullPath(hostMountSource).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return text
            .Replace(hostRoot, containerWorkspacePath.TrimEnd('/'), comparison)
            .Replace('\\', '/');
    }

    private static string MapHostPathIfUnderMount(
        string path,
        string hostMountSource,
        string containerWorkspacePath)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return path;
        }

        var hostRoot = Path.GetFullPath(hostMountSource)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(hostRoot, comparison))
        {
            return path;
        }

        var relativePath = Path.GetRelativePath(hostRoot, fullPath).Replace('\\', '/');
        return $"{containerWorkspacePath.TrimEnd('/')}/{relativePath.TrimStart('/')}";
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
            throw new InvalidOperationException("Python script worker command arguments contain an unterminated quote.");
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
}
