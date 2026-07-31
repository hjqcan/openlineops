namespace OpenLineOps.Recipes.Domain.Recipes;

public sealed record RecipeRevisionSnapshot
{
    public RecipeRevisionSnapshot(
        string recipeId,
        string versionId,
        string displayName,
        DateTimeOffset releasedAtUtc,
        IReadOnlyCollection<RecipeParameterSnapshot> parameters,
        string configurationSha256)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        RecipeId = RecipeValueGuard.Required(recipeId, nameof(recipeId));
        VersionId = RecipeValueGuard.Required(versionId, nameof(versionId));
        DisplayName = RecipeValueGuard.Required(displayName, nameof(displayName));
        ReleasedAtUtc = RecipeValueGuard.Utc(releasedAtUtc, nameof(releasedAtUtc));
        Parameters = parameters
            .OrderBy(static parameter => parameter.Key, StringComparer.Ordinal)
            .ToArray();
        if (Parameters.Count == 0)
        {
            throw new ArgumentException(
                "A released recipe revision must contain at least one parameter.",
                nameof(parameters));
        }

        if (Parameters.Select(static parameter => parameter.Key)
            .Distinct(StringComparer.Ordinal).Count() != Parameters.Count)
        {
            throw new ArgumentException(
                "Recipe parameter keys must be unique.",
                nameof(parameters));
        }

        var suppliedHash = RecipeValueGuard.Sha256(
            configurationSha256,
            nameof(configurationSha256));
        var computedHash = RecipeConfigurationIntegrity.Compute(
            RecipeId,
            VersionId,
            Parameters);
        if (!string.Equals(suppliedHash, computedHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Recipe configuration hash does not match the canonical typed parameters.",
                nameof(configurationSha256));
        }

        ConfigurationSha256 = computedHash;
    }

    public string RecipeId { get; }

    public string VersionId { get; }

    public string DisplayName { get; }

    public DateTimeOffset ReleasedAtUtc { get; }

    public IReadOnlyCollection<RecipeParameterSnapshot> Parameters { get; }

    public string ConfigurationSha256 { get; }
}
