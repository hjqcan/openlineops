using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Stations;

public sealed record StationRecipeStartAuthority(
    string RecipeId,
    string RecipeVersion,
    Guid AssignmentId,
    Guid DeploymentId,
    string ConfigurationSha256);

public sealed record StationRecipeStartAuthorityDecision(
    bool Allowed,
    StationRecipeStartAuthority? Authority,
    string Code,
    string Reason)
{
    public static StationRecipeStartAuthorityDecision Allow(
        StationRecipeStartAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return new(true, authority, "Runtime.StationRecipeAuthorityReady", "Ready.");
    }

    public static StationRecipeStartAuthorityDecision Reject(
        string code,
        string reason) =>
        new(false, null, Required(code, nameof(code)), Required(reason, nameof(reason)));

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", parameterName)
            : value.Trim();
}

public interface IStationRecipeStartAuthority
{
    ValueTask<StationRecipeStartAuthorityDecision> ResolveAsync(
        StationId stationId,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed class RejectingStationRecipeStartAuthority
    : IStationRecipeStartAuthority
{
    public ValueTask<StationRecipeStartAuthorityDecision> ResolveAsync(
        StationId stationId,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            StationRecipeStartAuthorityDecision.Reject(
                "Runtime.StationRecipeAuthorityUnavailable",
                $"Station {stationId} has no independently composed recipe "
                + "assignment, deployment, readback, and changeover authority."));
    }
}
