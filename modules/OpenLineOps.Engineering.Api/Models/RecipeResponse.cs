namespace OpenLineOps.Engineering.Api.Models;

public sealed record RecipeResponse(
    string RecipeId,
    string VersionId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? ValidatedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    string? ApprovedBy,
    DateTimeOffset? ReleasedAtUtc,
    DateTimeOffset? RetiredAtUtc,
    IReadOnlyCollection<RecipeParameterResponse> Parameters);
