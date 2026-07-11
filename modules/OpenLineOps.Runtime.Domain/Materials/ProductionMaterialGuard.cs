namespace OpenLineOps.Runtime.Domain.Materials;

internal static class ProductionMaterialGuard
{
    public static string Canonical(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName);
        }

        return value;
    }

    public static string? OptionalCanonical(string? value, string parameterName)
    {
        return value is null ? null : Canonical(value, parameterName);
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{parameterName} must use the UTC offset.",
                parameterName);
        }

        return value;
    }

    public static void RequireMonotonic(
        DateTimeOffset occurredAtUtc,
        DateTimeOffset lastTransitionAtUtc,
        string aggregateName)
    {
        Utc(occurredAtUtc, nameof(occurredAtUtc));
        if (occurredAtUtc < lastTransitionAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurredAtUtc),
                $"{aggregateName} transition time cannot precede its previous transition.");
        }
    }
}
