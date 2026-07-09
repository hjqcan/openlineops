namespace OpenLineOps.Traceability.Domain.Operations;

public sealed record TraceOperationResult(bool Succeeded, string Code, string Message)
{
    public static TraceOperationResult Accepted(string message)
    {
        return new TraceOperationResult(true, "Traceability.Accepted", message);
    }

    public static TraceOperationResult Rejected(string code, string message)
    {
        return new TraceOperationResult(false, code, message);
    }
}
