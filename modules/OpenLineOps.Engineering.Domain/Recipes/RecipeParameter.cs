using System.Globalization;

namespace OpenLineOps.Engineering.Domain.Recipes;

public sealed record RecipeParameter
{
    public RecipeParameter(string key, string value)
        : this(
            key,
            value,
            RecipeParameterType.String,
            unit: null,
            minimum: null,
            maximum: null,
            allowedValues: null,
            required: true)
    {
    }

    public RecipeParameter(
        string key,
        string value,
        RecipeParameterType type,
        string? unit,
        decimal? minimum,
        decimal? maximum,
        IEnumerable<string>? allowedValues,
        bool required)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        Key = NormalizeRequired(key, nameof(key));
        Type = type;
        Required = required;
        Unit = NormalizeOptional(unit);
        Minimum = minimum;
        Maximum = maximum;
        AllowedValues = NormalizeAllowedValues(allowedValues);

        ValidateSchema();
        Value = NormalizeValue(value);
        ValidateValue();
    }

    public string Key { get; }

    public string Value { get; }

    public RecipeParameterType Type { get; }

    public string? Unit { get; }

    public decimal? Minimum { get; }

    public decimal? Maximum { get; }

    public IReadOnlyCollection<string> AllowedValues { get; }

    public bool Required { get; }

    private void ValidateSchema()
    {
        if (Minimum is not null && Maximum is not null && Minimum > Maximum)
        {
            throw new ArgumentException(
                "Recipe parameter minimum cannot exceed maximum.",
                nameof(Minimum));
        }

        var isNumeric = Type is RecipeParameterType.Integer or RecipeParameterType.Decimal;
        if (!isNumeric && (Minimum is not null || Maximum is not null))
        {
            throw new ArgumentException(
                "Only numeric recipe parameters can define minimum or maximum values.",
                nameof(Minimum));
        }

        if (!isNumeric && Unit is not null)
        {
            throw new ArgumentException(
                "Only numeric recipe parameters can define an engineering unit.",
                nameof(Unit));
        }

        if (Type == RecipeParameterType.Enum && AllowedValues.Count == 0)
        {
            throw new ArgumentException(
                "Enum recipe parameters must define at least one allowed value.",
                nameof(AllowedValues));
        }

        if (Type != RecipeParameterType.Enum && AllowedValues.Count > 0)
        {
            throw new ArgumentException(
                "Only enum recipe parameters can define allowed values.",
                nameof(AllowedValues));
        }
    }

    private string NormalizeValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (Required)
            {
                throw new ArgumentException(
                    "Required recipe parameter value cannot be empty.",
                    nameof(value));
            }

            return string.Empty;
        }

        var trimmed = value.Trim();
        return Type switch
        {
            RecipeParameterType.Boolean => bool.TryParse(trimmed, out var parsedBoolean)
                ? parsedBoolean.ToString().ToLowerInvariant()
                : throw InvalidValue("a Boolean value"),
            RecipeParameterType.Integer => long.TryParse(
                trimmed,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedInteger)
                    ? parsedInteger.ToString(CultureInfo.InvariantCulture)
                    : throw InvalidValue("an invariant Integer value"),
            RecipeParameterType.Decimal => decimal.TryParse(
                trimmed,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsedDecimal)
                    ? parsedDecimal.ToString("G29", CultureInfo.InvariantCulture)
                    : throw InvalidValue("an invariant Decimal value"),
            _ => trimmed
        };
    }

    private void ValidateValue()
    {
        if (Value.Length == 0)
        {
            return;
        }

        if (Type == RecipeParameterType.Enum
            && !AllowedValues.Contains(Value, StringComparer.Ordinal))
        {
            throw InvalidValue(
                $"one of the configured enum values: {string.Join(", ", AllowedValues)}");
        }

        if (Type == RecipeParameterType.Integer)
        {
            var numericValue = long.Parse(Value, CultureInfo.InvariantCulture);
            ValidateRange(numericValue);
        }
        else if (Type == RecipeParameterType.Decimal)
        {
            var numericValue = decimal.Parse(Value, CultureInfo.InvariantCulture);
            ValidateRange(numericValue);
        }
    }

    private void ValidateRange(decimal numericValue)
    {
        if (Minimum is not null && numericValue < Minimum)
        {
            throw InvalidValue($"a value greater than or equal to {Minimum}");
        }

        if (Maximum is not null && numericValue > Maximum)
        {
            throw InvalidValue($"a value less than or equal to {Maximum}");
        }
    }

    private ArgumentException InvalidValue(string expectation)
    {
        return new ArgumentException(
            $"Recipe parameter '{Key}' must contain {expectation}.");
    }

    private static IReadOnlyCollection<string> NormalizeAllowedValues(
        IEnumerable<string>? allowedValues)
    {
        if (allowedValues is null)
        {
            return Array.Empty<string>();
        }

        var normalized = allowedValues
            .Select(value => NormalizeRequired(value, nameof(allowedValues)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return Array.AsReadOnly(normalized);
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Recipe parameter value cannot be empty.",
                parameterName);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
