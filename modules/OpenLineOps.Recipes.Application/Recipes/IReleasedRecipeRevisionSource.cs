using OpenLineOps.Recipes.Domain.Recipes;

namespace OpenLineOps.Recipes.Application.Recipes;

public interface IReleasedRecipeRevisionSource
{
    ValueTask<RecipeRevisionSnapshot?> GetReleasedAsync(
        string recipeId,
        string versionId,
        CancellationToken cancellationToken = default);
}
