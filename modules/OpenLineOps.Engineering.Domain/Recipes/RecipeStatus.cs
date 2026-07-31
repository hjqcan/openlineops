namespace OpenLineOps.Engineering.Domain.Recipes;

public enum RecipeStatus
{
    Draft = 0,
    Validated = 1,
    Approved = 2,
    Released = 3,
    Published = Released,
    Retired = 4
}
