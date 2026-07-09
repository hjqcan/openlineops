namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record RecipeDetails(
    string RecipeId,
    string VersionId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    IReadOnlyCollection<RecipeParameterDetails> Parameters);
