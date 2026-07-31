namespace OpenLineOps.Integration.Application;

internal static class ApplicationGuard
{
    public static string RequiredCanonical(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
               || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        return value == default || value.Offset != TimeSpan.Zero
            ? throw new ArgumentException(
                $"{parameterName} must be a non-default UTC timestamp.",
                parameterName)
            : value;
    }

    public static string Sha256(string value, string parameterName)
    {
        return value.Length != 64
               || value.Any(static character =>
                   character is not (>= '0' and <= '9')
                   and not (>= 'a' and <= 'f'))
            ? throw new ArgumentException(
                $"{parameterName} must be lowercase SHA-256 hexadecimal.",
                parameterName)
            : value;
    }
}
