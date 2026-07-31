namespace OpenLineOps.Engineering.Api.Models;

public sealed record RecipeParameterResponse(
    string Key,
    string Value,
    string Type,
    string? Unit,
    decimal? Minimum,
    decimal? Maximum,
    IReadOnlyCollection<string> AllowedValues,
    bool Required);
