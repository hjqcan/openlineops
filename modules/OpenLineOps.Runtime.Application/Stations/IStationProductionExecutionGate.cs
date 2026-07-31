using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Runtime.Application.Stations;

public interface IStationProductionExecutionGate
{
    ValueTask<StationProductionExecutionGateResult> EvaluateAsync(
        string stationSystemId,
        StationExecutionRecipeExpectation? expectedRecipe = null,
        CancellationToken cancellationToken = default);
}

public sealed record StationExecutionRecipeExpectation
{
    public StationExecutionRecipeExpectation(
        string recipeId,
        string recipeVersion)
    {
        RecipeId = Required(recipeId, nameof(recipeId));
        RecipeVersion = Required(recipeVersion, nameof(recipeVersion));
    }

    public string RecipeId { get; }

    public string RecipeVersion { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
}

public sealed record StationProductionExecutionGateResult(
    bool Managed,
    bool Allowed,
    string Reason,
    string? Evidence,
    DateTimeOffset? ValidUntilUtc = null,
    StationAgentControlLeaseDispatchAuthority? AgentControlLease = null,
    string? Revision = null);

/// <summary>
/// Test-only compatibility gate for isolated legacy runner tests that do not model a Station.
/// Production composition must use <see cref="StationProductionExecutionGate"/> and publication
/// authorization rejects unmanaged results even if this gate is supplied accidentally.
/// </summary>
public sealed class LegacyCompatibilityStationProductionExecutionGate
    : IStationProductionExecutionGate
{
    public static LegacyCompatibilityStationProductionExecutionGate Instance { get; } = new();

    private LegacyCompatibilityStationProductionExecutionGate()
    {
    }

    public ValueTask<StationProductionExecutionGateResult> EvaluateAsync(
        string stationSystemId,
        StationExecutionRecipeExpectation? expectedRecipe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationSystemId);
        _ = expectedRecipe;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new StationProductionExecutionGateResult(
            Managed: false,
            Allowed: true,
            "Legacy test composition bypassed Station execution readiness.",
            Evidence: null));
    }
}
