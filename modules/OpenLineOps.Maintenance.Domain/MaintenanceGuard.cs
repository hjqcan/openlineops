namespace OpenLineOps.Maintenance.Domain;

internal static class MaintenanceGuard
{
    public static string RequireCanonicalText(
        string value,
        string parameterName,
        int maximumLength = 160)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text no longer than "
                + $"{maximumLength} characters.",
                parameterName);
        }

        return value;
    }

    public static DateTimeOffset RequireUtc(
        DateTimeOffset value,
        string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{parameterName} must use the UTC offset.",
                parameterName);
        }

        return value;
    }
}
