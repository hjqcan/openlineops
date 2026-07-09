namespace OpenLineOps.Runtime.Domain.Operations;

public sealed record RuntimeOperationResult(bool Succeeded, string Code, string Message)
{
    public static RuntimeOperationResult Accepted(string message = "Accepted")
    {
        return new RuntimeOperationResult(true, "Runtime.Accepted", message);
    }

    public static RuntimeOperationResult Rejected(string code, string message)
    {
        return new RuntimeOperationResult(false, code, message);
    }
}
