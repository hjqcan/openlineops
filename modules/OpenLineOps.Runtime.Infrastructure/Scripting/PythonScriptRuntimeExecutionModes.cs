namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public static class PythonScriptRuntimeExecutionModes
{
    public const string InProcessTrusted = "InProcessTrusted";

    public const string ProcessIsolated = "ProcessIsolated";

    public static bool IsInProcessTrusted(string? value)
    {
        return string.Equals(value, InProcessTrusted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "InProcess", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Trusted", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsProcessIsolated(string? value)
    {
        return string.Equals(value, ProcessIsolated, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Worker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "ExternalProcess", StringComparison.OrdinalIgnoreCase);
    }
}
