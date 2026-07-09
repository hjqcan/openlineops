namespace OpenLineOps.SampleInspection.Domain.Operations;

public sealed record InspectionOperationResult(bool Succeeded, string Code, string Message)
{
    public static InspectionOperationResult Accepted(string message = "Accepted.")
    {
        return new InspectionOperationResult(true, "Inspection.Accepted", message);
    }

    public static InspectionOperationResult Rejected(string code, string message)
    {
        return new InspectionOperationResult(false, code, message);
    }
}
