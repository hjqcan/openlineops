namespace OpenLineOps.Agent.Infrastructure.Execution;

public sealed record StationRuntimePythonScriptOptions(
    string WorkerExecutablePath,
    string HostPythonRuntimeDllPath,
    StationRuntimePythonScriptSandboxOptions Sandbox);

public sealed record StationRuntimePythonScriptSandboxOptions(
    bool RequireLeastPrivilegeExecution,
    string IsolationMode,
    string? LeastPrivilegeIdentity = null,
    string? LeastPrivilegeLauncherExecutable = null,
    string? LeastPrivilegeArgumentsTemplate = null,
    bool LeastPrivilegeNoInteractivePrompt = true);

public static class StationRuntimePythonScriptIsolationModes
{
    public const string ExternalProcess = "ExternalProcess";

    public const string LeastPrivilegeIdentity = "LeastPrivilegeIdentity";
}
