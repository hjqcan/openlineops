namespace OpenLineOps.Engineering.Api.Models;

public sealed record RecipeParameterRequest(
    string? Key,
    string? Value,
    string Type = "String",
    string? Unit = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    IReadOnlyCollection<string>? AllowedValues = null,
    bool Required = true);
