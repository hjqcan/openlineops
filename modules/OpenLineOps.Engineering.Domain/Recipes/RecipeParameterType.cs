namespace OpenLineOps.Engineering.Domain.Recipes;

#pragma warning disable CA1720 // These names are the stable public recipe schema tokens.
public enum RecipeParameterType
{
    String = 0,
    Boolean = 1,
    Integer = 2,
    Decimal = 3,
    Enum = 4
}
#pragma warning restore CA1720
