namespace OpenLineOps.Runtime.Domain.Identifiers;

internal static class RuntimeIdGuard
{
    public static string NotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identifier value cannot be empty.", parameterName);
        }

        return value.Trim();
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
