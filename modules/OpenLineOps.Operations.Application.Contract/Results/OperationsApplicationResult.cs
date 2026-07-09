namespace OpenLineOps.Operations.Application.Contract.Results;

public sealed record OperationsApplicationResult(bool Succeeded, string Code, string Message)
{
    public static OperationsApplicationResult Accepted(string message = "Accepted.")
    {
        return new OperationsApplicationResult(true, "Operations.Accepted", message);
    }

    public static OperationsApplicationResult Rejected(string code, string message)
    {
        return new OperationsApplicationResult(false, code, message);
    }
}
