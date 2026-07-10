namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public sealed class PythonScriptRuntimeOptions
{
    public const string SectionName = "OpenLineOps:Runtime:Scripting:Python";

    public string ExecutionMode { get; set; } = PythonScriptRuntimeExecutionModes.ProcessIsolated;

    public string? WorkerFileName { get; set; }

    public string? WorkerArguments { get; set; }

    public string? WorkerWorkingDirectory { get; set; }

    public PythonScriptWorkerSandboxOptions Sandbox { get; set; } = new();
}

public sealed class PythonScriptWorkerSandboxOptions
{
    public bool RequireLeastPrivilegeExecution { get; set; }

    public string IsolationMode { get; set; } = PythonScriptWorkerIsolationModes.ExternalProcess;

    public string? LeastPrivilegeIdentity { get; set; }

    public string? LeastPrivilegeLauncherExecutable { get; set; }

    public string? LeastPrivilegeArgumentsTemplate { get; set; }

    public bool LeastPrivilegeNoInteractivePrompt { get; set; } = true;

    public string? ContainerImage { get; set; }

    public string? ContainerRuntimeExecutable { get; set; }

    public string? ContainerMountSource { get; set; }

    public string ContainerWorkspacePath { get; set; } = "/openlineops/script-worker";

    public string? ContainerWorkingDirectory { get; set; }

    public string? ContainerExecutablePath { get; set; }

    public string ContainerArgumentsTemplate { get; set; } = "{WorkerArguments}";

    public string ContainerNetwork { get; set; } = "none";

    public bool ContainerNoNewPrivileges { get; set; } = true;

    public bool ContainerDropAllCapabilities { get; set; } = true;

    public bool ContainerReadOnlyRootFilesystem { get; set; } = true;

    public bool ContainerMountReadOnly { get; set; } = true;

    public int ContainerPidsLimit { get; set; } = 128;

    public List<string> AdditionalContainerRunArguments { get; } = [];

    public bool HasLeastPrivilegeExecution
    {
        get
        {
            return PythonScriptWorkerIsolationModes.IsLeastPrivilegeIdentity(IsolationMode)
                || PythonScriptWorkerIsolationModes.IsContainer(IsolationMode)
                || !string.IsNullOrWhiteSpace(LeastPrivilegeIdentity)
                || !string.IsNullOrWhiteSpace(ContainerImage);
        }
    }
}

public static class PythonScriptWorkerIsolationModes
{
    public const string ExternalProcess = "ExternalProcess";

    public const string LeastPrivilegeIdentity = "LeastPrivilegeIdentity";

    public const string Container = "Container";

    public static PythonScriptWorkerIsolationMode Parse(string? value)
    {
        return value switch
        {
            ExternalProcess => PythonScriptWorkerIsolationMode.ExternalProcess,
            LeastPrivilegeIdentity => PythonScriptWorkerIsolationMode.LeastPrivilegeIdentity,
            Container => PythonScriptWorkerIsolationMode.Container,
            _ => throw new InvalidOperationException(
                $"Unsupported Python script worker isolation mode '{value}'. Expected exactly "
                + $"'{ExternalProcess}', '{LeastPrivilegeIdentity}', or '{Container}'.")
        };
    }

    public static bool IsLeastPrivilegeIdentity(string? value)
    {
        return string.Equals(value, LeastPrivilegeIdentity, StringComparison.Ordinal);
    }

    public static bool IsContainer(string? value)
    {
        return string.Equals(value, Container, StringComparison.Ordinal);
    }
}

public enum PythonScriptWorkerIsolationMode
{
    ExternalProcess,
    LeastPrivilegeIdentity,
    Container
}
