using OpenLineOps.Runtime.Domain.Commands;

namespace OpenLineOps.Runtime.Application.Commands;

public sealed record RuntimeCommandExecutionResult
{
    private RuntimeCommandExecutionResult(
        RuntimeCommandExecutionOutcome outcome,
        string? payload,
        string? reason,
        RuntimeCommandSemanticOutcome? semanticOutcome)
    {
        Outcome = outcome;
        Payload = payload;
        Reason = reason;
        SemanticOutcome = semanticOutcome;
    }

    public RuntimeCommandExecutionOutcome Outcome { get; }

    public string? Payload { get; }

    public string? Reason { get; }

    public RuntimeCommandSemanticOutcome? SemanticOutcome { get; }

    public static RuntimeCommandExecutionResult Completed(string? payload = null)
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Completed,
            payload,
            null,
            null);
    }

    public static RuntimeCommandExecutionResult SemanticPassed(string payload)
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Completed,
            RequiredPayload(payload),
            null,
            RuntimeCommandSemanticOutcome.Passed);
    }

    public static RuntimeCommandExecutionResult Failed(string reason)
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Failed,
            null,
            RequiredReason(reason),
            null);
    }

    public static RuntimeCommandExecutionResult SemanticFailed(string reason, string payload)
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Failed,
            RequiredPayload(payload),
            RequiredReason(reason),
            RuntimeCommandSemanticOutcome.Failed);
    }

    public static RuntimeCommandExecutionResult Rejected(string reason)
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Rejected,
            null,
            RequiredReason(reason),
            null);
    }

    public static RuntimeCommandExecutionResult TimedOut(string reason = "Command timed out.")
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.TimedOut,
            null,
            RequiredReason(reason),
            null);
    }

    public static RuntimeCommandExecutionResult Canceled(string reason = "Command canceled.")
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Canceled,
            null,
            RequiredReason(reason),
            null);
    }

    public static RuntimeCommandExecutionResult SemanticAborted(string reason, string payload)
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Canceled,
            RequiredPayload(payload),
            RequiredReason(reason),
            RuntimeCommandSemanticOutcome.Aborted);
    }

    private static string RequiredReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            || char.IsWhiteSpace(reason[0])
            || char.IsWhiteSpace(reason[^1])
            ? throw new ArgumentException(
                "Command result reason must be non-empty canonical text.",
                nameof(reason))
            : reason;
    }

    private static string RequiredPayload(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload;
    }
}
