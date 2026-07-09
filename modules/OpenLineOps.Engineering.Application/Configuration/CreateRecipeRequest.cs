namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record CreateRecipeRequest(
    string RecipeId,
    string VersionId,
    string DisplayName,
    IReadOnlyCollection<RecipeParameterRequest> Parameters);
