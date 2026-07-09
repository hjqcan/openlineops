namespace OpenLineOps.Engineering.Domain.Operations;

public sealed record EngineeringOperationResult(bool Succeeded, string Code, string Message)
{
    public static EngineeringOperationResult Accepted(string message = "Accepted")
    {
        return new EngineeringOperationResult(true, "Engineering.Accepted", message);
    }

    public static EngineeringOperationResult Rejected(string code, string message)
    {
        return new EngineeringOperationResult(false, code, message);
    }
}
