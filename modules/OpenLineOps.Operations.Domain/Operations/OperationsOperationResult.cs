namespace OpenLineOps.Operations.Domain.Operations;

public sealed record OperationsOperationResult(bool Succeeded, string Code, string Message)
{
    public static OperationsOperationResult Accepted(string message = "Accepted.")
    {
        return new OperationsOperationResult(true, "Operations.Accepted", message);
    }

    public static OperationsOperationResult Rejected(string code, string message)
    {
        return new OperationsOperationResult(false, code, message);
    }
}
