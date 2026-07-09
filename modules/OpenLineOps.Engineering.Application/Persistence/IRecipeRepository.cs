using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Recipes;

namespace OpenLineOps.Engineering.Application.Persistence;

public interface IRecipeRepository
{
    Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default);

    Task<Recipe?> GetByIdAsync(RecipeId recipeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Recipe>> ListAsync(CancellationToken cancellationToken = default);
}
