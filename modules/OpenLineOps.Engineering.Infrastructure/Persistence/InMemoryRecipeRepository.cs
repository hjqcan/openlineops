using System.Collections.Concurrent;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Recipes;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

public sealed class InMemoryRecipeRepository : IRecipeRepository
{
    private readonly ConcurrentDictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);

    public Task SaveAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        cancellationToken.ThrowIfCancellationRequested();

        _recipes[recipe.Id.Value] = recipe;

        return Task.CompletedTask;
    }

    public Task<Recipe?> GetByIdAsync(RecipeId recipeId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipeId);
        cancellationToken.ThrowIfCancellationRequested();

        _recipes.TryGetValue(recipeId.Value, out var recipe);

        return Task.FromResult(recipe);
    }

    public Task<IReadOnlyCollection<Recipe>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyCollection<Recipe>>(_recipes.Values.ToArray());
    }
}
