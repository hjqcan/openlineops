namespace OpenLineOps.Recipes.Application.Readiness;

public sealed record RecipeProductionReadinessRequest(
    string StationId,
    string ProductModelId,
    string RecipeId,
    string VersionId,
    DateTimeOffset EvaluatedAtUtc);

public sealed record RecipeProductionReadinessBlock(
    string Code,
    string Detail);

public sealed record RecipeProductionReadinessResult(
    bool Allowed,
    Guid? AssignmentId,
    Guid? DeploymentId,
    string? ConfigurationSha256,
    IReadOnlyCollection<RecipeProductionReadinessBlock> Blocks);

public interface IRecipeProductionReadinessGate
{
    ValueTask<RecipeProductionReadinessResult> EvaluateAsync(
        RecipeProductionReadinessRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StationRecipeAuthority(
    string StationId,
    string ProductModelId,
    string RecipeId,
    string VersionId,
    Guid AssignmentId,
    Guid DeploymentId,
    string ConfigurationSha256,
    DateTimeOffset ChangeoverCompletedAtUtc);

public sealed record StationRecipeAuthorityResult(
    bool Allowed,
    StationRecipeAuthority? Authority,
    IReadOnlyCollection<RecipeProductionReadinessBlock> Blocks);

public interface IStationRecipeAuthorityResolver
{
    ValueTask<StationRecipeAuthorityResult> ResolveAsync(
        string stationId,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default);
}
