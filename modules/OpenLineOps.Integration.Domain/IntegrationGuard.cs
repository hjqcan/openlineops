namespace OpenLineOps.Integration.Domain;

internal static class IntegrationGuard
{
    public static string RequiredCanonical(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
               || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
               || value.Length > 256
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text no longer than 256 characters.",
                parameterName)
            : value;
    }

    public static string? OptionalCanonical(string? value, string parameterName)
    {
        return value is null ? null : RequiredCanonical(value, parameterName);
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        return value == default || value.Offset != TimeSpan.Zero
            ? throw new ArgumentException(
                $"{parameterName} must be a non-default UTC timestamp.",
                parameterName)
            : value;
    }

    public static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        return !Enum.IsDefined(value)
            ? throw new ArgumentOutOfRangeException(parameterName, value, "Enum value is not defined.")
            : value;
    }
}
