namespace OpenLineOps.Engineering.Domain.Recipes;

public sealed record RecipeParameter
{
    public RecipeParameter(string key, string value)
    {
        Key = Normalize(key, nameof(key));
        Value = Normalize(value, nameof(value));
    }

    public string Key { get; }

    public string Value { get; }

    private static string Normalize(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Recipe parameter value cannot be empty.", parameterName);
        }

        return value.Trim();
    }
}
