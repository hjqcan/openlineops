namespace OpenLineOps.Plugins.Application.Commands;

public sealed record PluginProcessCommandInvocationResult(
    PluginProcessCommandInvocationOutcome Outcome,
    string? ResultPayload,
    string? FailureReason)
{
    public bool Succeeded => Outcome == PluginProcessCommandInvocationOutcome.Completed;

    public static PluginProcessCommandInvocationResult Completed(string? resultPayload = null)
    {
        return new PluginProcessCommandInvocationResult(
            PluginProcessCommandInvocationOutcome.Completed,
            resultPayload,
            null);
    }

    public static PluginProcessCommandInvocationResult Failed(string failureReason)
    {
        return new PluginProcessCommandInvocationResult(
            PluginProcessCommandInvocationOutcome.Failed,
            null,
            NormalizeReason(failureReason));
    }

    public static PluginProcessCommandInvocationResult Rejected(string failureReason)
    {
        return new PluginProcessCommandInvocationResult(
            PluginProcessCommandInvocationOutcome.Rejected,
            null,
            NormalizeReason(failureReason));
    }

    public static PluginProcessCommandInvocationResult TimedOut(string failureReason)
    {
        return new PluginProcessCommandInvocationResult(
            PluginProcessCommandInvocationOutcome.TimedOut,
            null,
            NormalizeReason(failureReason));
    }

    public static PluginProcessCommandInvocationResult Canceled(string failureReason)
    {
        return new PluginProcessCommandInvocationResult(
            PluginProcessCommandInvocationOutcome.Canceled,
            null,
            NormalizeReason(failureReason));
    }

    private static string NormalizeReason(string failureReason)
    {
        return string.IsNullOrWhiteSpace(failureReason)
            ? throw new ArgumentException("Failure reason is required.", nameof(failureReason))
            : failureReason.Trim();
    }
}
