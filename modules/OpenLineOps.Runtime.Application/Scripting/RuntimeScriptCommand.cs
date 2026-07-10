using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Runtime.Application.Scripting;

public static class RuntimeScriptCommand
{
    public const string PythonCapability = "process.python-script";

    public const string PythonCommandName = "PythonScript.Execute";

    public static bool IsPythonScript(RuntimeCommandExecutionContext context)
    {
        return string.Equals(context.TargetCapability.Value, PythonCapability, StringComparison.Ordinal)
            && string.Equals(context.CommandName, PythonCommandName, StringComparison.Ordinal);
    }
}
