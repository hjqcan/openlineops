namespace OpenLineOps.Commissioning.Domain;

internal static class CommissioningGuard
{
    public static string Canonical(string value, string parameterName)
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
        return value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException(
                $"{parameterName} must use UTC offset zero.",
                parameterName);
    }
}
