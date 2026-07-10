using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public sealed record PythonScriptWorkerExecutionResult(
    string Outcome,
    string? Payload,
    string? Reason)
{
    public static PythonScriptWorkerExecutionResult FromRuntimeResult(
        RuntimeCommandExecutionResult result)
    {
        return new PythonScriptWorkerExecutionResult(
            result.Outcome.ToString(),
            result.Payload,
            result.Reason);
    }

    public RuntimeCommandExecutionResult ToRuntimeResult()
    {
        if (!CanonicalEnumToken.TryParse<RuntimeCommandExecutionOutcome>(Outcome, out var parsed))
        {
            return RuntimeCommandExecutionResult.Failed(
                $"Python script worker returned unsupported outcome '{Outcome}'. " +
                $"Expected an exact, case-sensitive token: " +
                $"{CanonicalEnumToken.ExpectedTokens<RuntimeCommandExecutionOutcome>()}.");
        }

        return parsed switch
        {
            RuntimeCommandExecutionOutcome.Completed => RuntimeCommandExecutionResult.Completed(Payload),
            RuntimeCommandExecutionOutcome.Failed => RuntimeCommandExecutionResult.Failed(
                Reason ?? "Python script worker failed."),
            RuntimeCommandExecutionOutcome.Rejected => RuntimeCommandExecutionResult.Rejected(
                Reason ?? "Python script worker rejected the command."),
            RuntimeCommandExecutionOutcome.TimedOut => RuntimeCommandExecutionResult.TimedOut(
                Reason ?? "Python script worker timed out."),
            RuntimeCommandExecutionOutcome.Canceled => RuntimeCommandExecutionResult.Canceled(
                Reason ?? "Python script worker canceled the command."),
            _ => RuntimeCommandExecutionResult.Failed(
                $"Python script worker returned unsupported outcome '{Outcome}'.")
        };
    }
}
