namespace OpenLineOps.SampleInspection.Domain.Identifiers;

public sealed record InspectionPlanId
{
    public InspectionPlanId(string value)
    {
        Value = InspectionIdGuard.NotBlank(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString()
    {
        return Value;
    }
}
