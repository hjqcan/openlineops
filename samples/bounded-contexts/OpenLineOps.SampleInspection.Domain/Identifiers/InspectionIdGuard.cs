namespace OpenLineOps.SampleInspection.Domain.Identifiers;

internal static class InspectionIdGuard
{
    public static string NotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identifier values cannot be blank.", parameterName);
        }

        return value.Trim();
    }
}
