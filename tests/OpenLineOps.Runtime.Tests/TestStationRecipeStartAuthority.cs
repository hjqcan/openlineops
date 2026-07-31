using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Tests;

internal sealed class TestStationRecipeStartAuthority(
    string recipeId = "recipe-a",
    string recipeVersion = "1") : IStationRecipeStartAuthority
{
    private static readonly Guid AssignmentId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeploymentId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string ConfigurationSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public ValueTask<StationRecipeStartAuthorityDecision> ResolveAsync(
        StationId stationId,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            StationRecipeStartAuthorityDecision.Allow(
                new StationRecipeStartAuthority(
                    recipeId,
                    recipeVersion,
                    AssignmentId,
                    DeploymentId,
                    ConfigurationSha256)));
    }
}
