using System.Globalization;

namespace OpenLineOps.Recipes.Domain;

internal static class RecipeValueGuard
{
    public static string Required(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Value must be canonical non-empty text.",
                parameterName);
        }

        return value;
    }

    public static string? Optional(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Optional text must be canonical when supplied.");
        }

        return value;
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use UTC offset zero.", parameterName);
        }

        return value;
    }

    public static string Sha256(string value, string parameterName)
    {
        var canonical = Required(value, parameterName);
        if (canonical.Length != 64
            || canonical.Any(static character =>
                character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "SHA-256 must be 64 lowercase hexadecimal characters.",
                parameterName);
        }

        return canonical;
    }

    public static string CanonicalParameterValue(
        string value,
        Recipes.RecipeParameterValueType type,
        string parameterName,
        bool allowEmpty = false)
    {
        if (allowEmpty && string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var canonical = Required(value, parameterName);
        return type switch
        {
            Recipes.RecipeParameterValueType.Boolean
                when bool.TryParse(canonical, out var parsedBoolean)
                && string.Equals(
                    canonical,
                    parsedBoolean.ToString().ToLowerInvariant(),
                    StringComparison.Ordinal) => canonical,
            Recipes.RecipeParameterValueType.Integer
                when long.TryParse(
                    canonical,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedInteger)
                && string.Equals(
                    canonical,
                    parsedInteger.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) => canonical,
            Recipes.RecipeParameterValueType.Decimal
                when decimal.TryParse(
                    canonical,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var parsedDecimal)
                && string.Equals(
                    canonical,
                    parsedDecimal.ToString("G29", CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) => canonical,
            Recipes.RecipeParameterValueType.Boolean
                or Recipes.RecipeParameterValueType.Integer
                or Recipes.RecipeParameterValueType.Decimal =>
                throw new ArgumentException(
                    "Parameter value is not canonical for its declared type.",
                    parameterName),
            _ => canonical
        };
    }
}
