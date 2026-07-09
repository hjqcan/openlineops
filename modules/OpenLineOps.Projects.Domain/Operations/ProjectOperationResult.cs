namespace OpenLineOps.Projects.Domain.Operations;

public sealed record ProjectOperationResult(bool Succeeded, string Code, string Message)
{
    public static ProjectOperationResult Accepted(string message = "Accepted")
    {
        return new ProjectOperationResult(true, "Projects.Accepted", message);
    }

    public static ProjectOperationResult Rejected(string code, string message)
    {
        return new ProjectOperationResult(false, code, message);
    }
}
