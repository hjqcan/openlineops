namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record RecipeParameterDetails(
    string Key,
    string Value,
    string Type,
    string? Unit,
    decimal? Minimum,
    decimal? Maximum,
    IReadOnlyCollection<string> AllowedValues,
    bool Required);
