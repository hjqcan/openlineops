namespace OpenLineOps.Recipes.Domain.Recipes;

public sealed record RecipeParameterSnapshot
{
    public RecipeParameterSnapshot(
        string key,
        RecipeParameterValueType type,
        string? unit,
        string canonicalValue,
        bool required = true)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        Key = RecipeValueGuard.Required(key, nameof(key));
        Type = type;
        Required = required;
        Unit = RecipeValueGuard.Optional(unit);
        CanonicalValue = RecipeValueGuard.CanonicalParameterValue(
            canonicalValue,
            type,
            nameof(canonicalValue),
            allowEmpty: !required);

        var numeric = type is RecipeParameterValueType.Integer
            or RecipeParameterValueType.Decimal;
        if (!numeric && Unit is not null)
        {
            throw new ArgumentException(
                "Only numeric recipe parameters can declare an engineering unit.",
                nameof(unit));
        }
    }

    public string Key { get; }

    public RecipeParameterValueType Type { get; }

    public bool Required { get; }

    public string? Unit { get; }

    public string CanonicalValue { get; }
}
