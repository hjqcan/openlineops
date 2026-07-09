namespace OpenLineOps.Plugin.Abstractions;

public sealed record PluginProcessCommandExecutionResult(
    PluginProcessCommandExecutionOutcome Outcome,
    string? ResultPayload,
    string? FailureReason)
{
    public bool Succeeded => Outcome == PluginProcessCommandExecutionOutcome.Completed;

    public static PluginProcessCommandExecutionResult Completed(string? resultPayload = null)
    {
        return new PluginProcessCommandExecutionResult(
            PluginProcessCommandExecutionOutcome.Completed,
            resultPayload,
            null);
    }

    public static PluginProcessCommandExecutionResult Failed(string failureReason)
    {
        return new PluginProcessCommandExecutionResult(
            PluginProcessCommandExecutionOutcome.Failed,
            null,
            NormalizeReason(failureReason));
    }

    public static PluginProcessCommandExecutionResult Rejected(string failureReason)
    {
        return new PluginProcessCommandExecutionResult(
            PluginProcessCommandExecutionOutcome.Rejected,
            null,
            NormalizeReason(failureReason));
    }

    public static PluginProcessCommandExecutionResult TimedOut(string failureReason)
    {
        return new PluginProcessCommandExecutionResult(
            PluginProcessCommandExecutionOutcome.TimedOut,
            null,
            NormalizeReason(failureReason));
    }

    public static PluginProcessCommandExecutionResult Canceled(string failureReason)
    {
        return new PluginProcessCommandExecutionResult(
            PluginProcessCommandExecutionOutcome.Canceled,
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
