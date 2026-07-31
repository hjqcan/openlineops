using System.Globalization;

namespace OpenLineOps.Plugin.Abstractions;

public enum PluginDeviceValueType
{
    Null = 0,
    Boolean = 1,
    SignedInteger = 2,
    FloatingPoint = 3,
    FixedPoint = 4,
    Text = 5,
    Timestamp = 6,
    Binary = 7
}

public sealed record PluginDeviceValue
{
    public PluginDeviceValue(PluginDeviceValueType type, string canonicalValue)
    {
        Type = PluginDeviceContractGuard.Defined(type, nameof(type));
        ArgumentNullException.ThrowIfNull(canonicalValue);
        CanonicalValue = ValidateCanonicalValue(Type, canonicalValue);
    }

    public PluginDeviceValueType Type { get; }

    public string CanonicalValue { get; }

    public static PluginDeviceValue Null() => new(PluginDeviceValueType.Null, string.Empty);

    public static PluginDeviceValue FromBoolean(bool value) =>
        new(PluginDeviceValueType.Boolean, value ? "true" : "false");

    public static PluginDeviceValue FromInt64(long value) =>
        new(PluginDeviceValueType.SignedInteger, value.ToString(CultureInfo.InvariantCulture));

    public static PluginDeviceValue FromDouble(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Device double values must be finite.");
        }

        return new PluginDeviceValue(
            PluginDeviceValueType.FloatingPoint,
            value.ToString("R", CultureInfo.InvariantCulture));
    }

    public static PluginDeviceValue FromDecimal(decimal value) =>
        new(PluginDeviceValueType.FixedPoint, value.ToString("G29", CultureInfo.InvariantCulture));

    public static PluginDeviceValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new PluginDeviceValue(PluginDeviceValueType.Text, value);
    }

    public static PluginDeviceValue FromDateTimeOffset(DateTimeOffset value)
    {
        PluginDeviceContractGuard.Utc(value, nameof(value));
        return new PluginDeviceValue(
            PluginDeviceValueType.Timestamp,
            value.ToString("O", CultureInfo.InvariantCulture));
    }

    public static PluginDeviceValue FromBinary(ReadOnlySpan<byte> value) =>
        new(PluginDeviceValueType.Binary, Convert.ToBase64String(value));

    private static string ValidateCanonicalValue(PluginDeviceValueType type, string value)
    {
        return type switch
        {
            PluginDeviceValueType.Null when value.Length == 0 => value,
            PluginDeviceValueType.Boolean when value is "true" or "false" => value,
            PluginDeviceValueType.SignedInteger => ValidateInt64(value),
            PluginDeviceValueType.FloatingPoint => ValidateDouble(value),
            PluginDeviceValueType.FixedPoint => ValidateDecimal(value),
            PluginDeviceValueType.Text => value,
            PluginDeviceValueType.Timestamp => ValidateDateTimeOffset(value),
            PluginDeviceValueType.Binary => ValidateBinary(value),
            _ => throw new ArgumentException(
                $"{value} is not a canonical {type} value.",
                nameof(value))
        };
    }

    private static string ValidateInt64(string value)
    {
        return long.TryParse(
                   value,
                   NumberStyles.AllowLeadingSign,
                   CultureInfo.InvariantCulture,
                   out var parsed)
               && string.Equals(
                   value,
                   parsed.ToString(CultureInfo.InvariantCulture),
                   StringComparison.Ordinal)
            ? value
            : throw InvalidCanonicalValue(value, PluginDeviceValueType.SignedInteger);
    }

    private static string ValidateDouble(string value)
    {
        return double.TryParse(
                   value,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var parsed)
               && double.IsFinite(parsed)
               && string.Equals(
                   value,
                   parsed.ToString("R", CultureInfo.InvariantCulture),
                   StringComparison.Ordinal)
            ? value
            : throw InvalidCanonicalValue(value, PluginDeviceValueType.FloatingPoint);
    }

    private static string ValidateDecimal(string value)
    {
        return decimal.TryParse(
                   value,
                   NumberStyles.Number,
                   CultureInfo.InvariantCulture,
                   out var parsed)
               && string.Equals(
                   value,
                   parsed.ToString("G29", CultureInfo.InvariantCulture),
                   StringComparison.Ordinal)
            ? value
            : throw InvalidCanonicalValue(value, PluginDeviceValueType.FixedPoint);
    }

    private static string ValidateDateTimeOffset(string value)
    {
        return DateTimeOffset.TryParseExact(
                   value,
                   "O",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var parsed)
               && parsed.Offset == TimeSpan.Zero
               && string.Equals(
                   value,
                   parsed.ToString("O", CultureInfo.InvariantCulture),
                   StringComparison.Ordinal)
            ? value
            : throw InvalidCanonicalValue(value, PluginDeviceValueType.Timestamp);
    }

    private static string ValidateBinary(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            return string.Equals(value, Convert.ToBase64String(bytes), StringComparison.Ordinal)
                ? value
                : throw InvalidCanonicalValue(value, PluginDeviceValueType.Binary);
        }
        catch (FormatException)
        {
            throw InvalidCanonicalValue(value, PluginDeviceValueType.Binary);
        }
    }

    private static ArgumentException InvalidCanonicalValue(
        string value,
        PluginDeviceValueType type) =>
        new($"{value} is not a canonical {type} value.", nameof(value));
}
