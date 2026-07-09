namespace OpenLineOps.Engineering.Api.Models;

public sealed record CreateRecipeRequest(
    string? RecipeId,
    string? VersionId,
    string? DisplayName,
    IReadOnlyCollection<RecipeParameterRequest>? Parameters);
