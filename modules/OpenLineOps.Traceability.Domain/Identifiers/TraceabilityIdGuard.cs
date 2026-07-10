namespace OpenLineOps.Traceability.Domain.Identifiers;

internal static class TraceabilityIdGuard
{
    public static string NotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException("Identifier value must be a non-empty canonical string.", parameterName);
        }

        return value;
    }

    public static string? OptionalText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException("Optional text must be null or a non-empty canonical string.", nameof(value));
        }

        return value;
    }

    public static string? OptionalSha256(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length != 64
            || !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException(
                "SHA-256 must be a lowercase 64-character hexadecimal value.",
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
