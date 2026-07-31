namespace OpenLineOps.Plugin.Abstractions;

internal static class PluginDeviceContractGuard
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

    public static string? OptionalCanonical(string? value, string parameterName)
    {
        return value is null ? null : RequiredCanonical(value, parameterName);
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        return value.Offset != TimeSpan.Zero
            ? throw new ArgumentException(
                $"{parameterName} must use UTC offset zero.",
                parameterName)
            : value;
    }

    public static TimeSpan PositiveWholeMilliseconds(TimeSpan value, string parameterName)
    {
        return value <= TimeSpan.Zero
               || value.Ticks % TimeSpan.TicksPerMillisecond != 0
            ? throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{parameterName} must be positive and use whole-millisecond precision.")
            : value;
    }

    public static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        return !Enum.IsDefined(value)
            ? throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"{parameterName} must be a defined {typeof(TEnum).Name} value.")
            : value;
    }

    public static IReadOnlyList<string> UniqueCanonicalIds(
        IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);

        var snapshot = values
            .Select(value => RequiredCanonical(value, parameterName))
            .ToArray();

        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                $"{parameterName} must contain at least one value.",
                parameterName);
        }

        if (snapshot.Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException(
                $"{parameterName} must contain unique canonical values.",
                parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }
}
