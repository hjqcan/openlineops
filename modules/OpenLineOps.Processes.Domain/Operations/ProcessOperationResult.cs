namespace OpenLineOps.Processes.Domain.Operations;

public sealed record ProcessOperationResult(bool Succeeded, string Code, string Message)
{
    public static ProcessOperationResult Accepted(string message = "Accepted")
    {
        return new ProcessOperationResult(true, "Processes.Accepted", message);
    }

    public static ProcessOperationResult Rejected(string code, string message)
    {
        return new ProcessOperationResult(false, code, message);
    }
}
