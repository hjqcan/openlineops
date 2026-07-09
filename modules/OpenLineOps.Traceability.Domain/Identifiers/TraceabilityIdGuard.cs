namespace OpenLineOps.Traceability.Domain.Identifiers;

internal static class TraceabilityIdGuard
{
    public static string NotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identifier value cannot be empty.", parameterName);
        }

        return value.Trim();
    }

    public static string? OptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    public static Guid NotEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier value cannot be empty.", parameterName);
        }

        return value;
    }
}
