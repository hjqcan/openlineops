using CommandResultJudgement = OpenLineOps.Runtime.Contracts.ResultJudgement;

namespace OpenLineOps.Runtime.Application.Commands;

public sealed record RuntimeCommandExecutionResult
{
    private RuntimeCommandExecutionResult(
        RuntimeCommandExecutionOutcome outcome,
        string? payload,
        string? reason,
        CommandResultJudgement resultJudgement)
    {
        Outcome = outcome;
        Payload = payload;
        Reason = reason;
        ResultJudgement = resultJudgement;
    }

    public RuntimeCommandExecutionOutcome Outcome { get; }

    public string? Payload { get; }

    public string? Reason { get; }

    public CommandResultJudgement ResultJudgement { get; }

    public static RuntimeCommandExecutionResult Completed(
        string? payload = null,
        CommandResultJudgement resultJudgement = CommandResultJudgement.NotApplicable)
    {
        if (!Enum.IsDefined(resultJudgement)
            || resultJudgement == CommandResultJudgement.Unknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resultJudgement),
                resultJudgement,
                "Completed execution requires a definite or not-applicable result judgement.");
        }

        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Completed,
            payload,
            null,
            resultJudgement);
    }

    public static RuntimeCommandExecutionResult Failed(string reason, string? payload = null)
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Failed,
            payload,
            RequiredReason(reason),
            CommandResultJudgement.Unknown);
    }

    public static RuntimeCommandExecutionResult Rejected(string reason)
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Rejected,
            null,
            RequiredReason(reason),
            CommandResultJudgement.Unknown);
    }

    public static RuntimeCommandExecutionResult TimedOut(
        string reason = "Command timed out.",
        string? payload = null)
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.TimedOut,
            payload,
            RequiredReason(reason),
            CommandResultJudgement.Unknown);
    }

    public static RuntimeCommandExecutionResult Canceled(
        string reason = "Command canceled.",
        string? payload = null)
    {
        return new RuntimeCommandExecutionResult(
            RuntimeCommandExecutionOutcome.Canceled,
            payload,
            RequiredReason(reason),
            CommandResultJudgement.Aborted);
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

}
