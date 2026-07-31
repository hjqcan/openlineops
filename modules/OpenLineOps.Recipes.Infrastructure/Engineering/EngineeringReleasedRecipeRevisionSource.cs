using Microsoft.Extensions.Options;
using OpenLineOps.Engineering.Application.ProjectWorkspaces;
using OpenLineOps.Recipes.Application.Hashing;
using OpenLineOps.Recipes.Application.Recipes;
using OpenLineOps.Recipes.Domain.Recipes;
using OpenLineOps.Recipes.Infrastructure.Persistence;

namespace OpenLineOps.Recipes.Infrastructure.Engineering;

public sealed class EngineeringReleasedRecipeRevisionSource(
    IProjectEngineeringConfigurationService engineeringService,
    IOptions<RecipePersistenceOptions> options)
    : IReleasedRecipeRevisionSource
{
    public async ValueTask<RecipeRevisionSnapshot?> GetReleasedAsync(
        string recipeId,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.EngineeringProjectId)
            || string.IsNullOrWhiteSpace(configured.EngineeringApplicationId))
        {
            return null;
        }

        var result = await engineeringService.GetRecipeAsync(
                configured.EngineeringProjectId,
                configured.EngineeringApplicationId,
                recipeId,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure
            || !string.Equals(result.Value.Status, "Released", StringComparison.Ordinal)
            || !string.Equals(result.Value.VersionId, versionId, StringComparison.Ordinal)
            || result.Value.ReleasedAtUtc is null)
        {
            return null;
        }

        var parameters = result.Value.Parameters
            .Select(parameter => new RecipeParameterSnapshot(
                parameter.Key,
                Enum.Parse<RecipeParameterValueType>(
                    parameter.Type,
                    ignoreCase: false),
                parameter.Unit,
                parameter.Value,
                parameter.Required))
            .ToArray();
        var configurationSha256 = RecipeConfigurationHasher.Compute(
            result.Value.RecipeId,
            result.Value.VersionId,
            parameters);
        return new RecipeRevisionSnapshot(
            result.Value.RecipeId,
            result.Value.VersionId,
            result.Value.DisplayName,
            result.Value.ReleasedAtUtc.Value,
            parameters,
            configurationSha256);
    }
}
