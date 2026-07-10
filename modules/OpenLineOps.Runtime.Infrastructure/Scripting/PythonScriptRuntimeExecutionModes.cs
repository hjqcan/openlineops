namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public static class PythonScriptRuntimeExecutionModes
{
    public const string ProcessIsolated = "ProcessIsolated";

    public static void RequireCurrent(string? value)
    {
        if (!string.Equals(value, ProcessIsolated, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported Python script execution mode '{value}'. Expected exactly "
                + $"'{ProcessIsolated}'.");
        }
    }
}
