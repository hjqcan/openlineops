namespace OpenLineOps.Recipes.Domain.Recipes;

#pragma warning disable CA1720 // These names are stable public recipe schema tokens.
public enum RecipeParameterValueType
{
    String = 0,
    Boolean = 1,
    Integer = 2,
    Decimal = 3,
    Enum = 4
}
#pragma warning restore CA1720
