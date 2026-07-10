namespace OpenLineOps.Runtime.Application.Commands;

public static class RuntimeFlowCommand
{
    public const string Capability = "runtime.flow";

    public const string WaitCommandName = "Wait";

    public static bool IsWait(RuntimeCommandExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return string.Equals(context.TargetCapability.Value, Capability, StringComparison.OrdinalIgnoreCase)
            && string.Equals(context.CommandName, WaitCommandName, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record RuntimeFlowWaitCommandPayload(double DurationMilliseconds);
