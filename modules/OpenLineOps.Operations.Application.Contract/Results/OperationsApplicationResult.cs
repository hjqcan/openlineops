namespace OpenLineOps.Operations.Application.Contract.Results;

public sealed record OperationsApplicationResult(
    bool Succeeded,
    string Code,
    string Message,
    long? Version = null,
    bool Replayed = false)
{
    public static OperationsApplicationResult Accepted(
        string message = "Accepted.",
        long? version = null,
        bool replayed = false)
    {
        return new OperationsApplicationResult(
            true,
            "Operations.Accepted",
            message,
            version,
            replayed);
    }

    public static OperationsApplicationResult Rejected(string code, string message)
    {
        return new OperationsApplicationResult(false, code, message);
    }
}
