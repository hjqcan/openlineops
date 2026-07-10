namespace OpenLineOps.Runtime.Domain.Identifiers;

internal static class RuntimeIdGuard
{
    public static string NotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                "Identifier value must be non-empty canonical text.",
                parameterName);
        }

        return value;
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
