using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Application.Stations;

public sealed class StationProductionExecutionGate(
    IStationLifecycleRepository repository) : IStationProductionExecutionGate
{
    public async ValueTask<StationProductionExecutionGateResult> EvaluateAsync(
        string stationSystemId,
        CancellationToken cancellationToken = default)
    {
        var stationId = new StationId(stationSystemId);
        var entry = await repository.GetByIdAsync(stationId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return new StationProductionExecutionGateResult(
                Managed: false,
                Allowed: true,
                "Station lifecycle has not been enrolled; compatibility execution rules apply.",
                Evidence: null);
        }

        var station = entry.Station;
        if (station.Mode is not StationMode.Automatic and not StationMode.Simulation)
        {
            return Rejected(
                station,
                entry.Revision,
                $"Station mode {station.Mode} does not permit production dispatch.");
        }

        if (station.State != StationState.Execute)
        {
            return Rejected(
                station,
                entry.Revision,
                $"Station state {station.State} is not Execute.");
        }

        var missing = station.Readiness.GetMissing(
            StationReadinessRequirement.ReadyToExecute);
        if (missing.Count > 0)
        {
            return Rejected(
                station,
                entry.Revision,
                $"Station readiness is missing: {string.Join(", ", missing)}.");
        }

        return new StationProductionExecutionGateResult(
            Managed: true,
            Allowed: true,
            "Station lifecycle permits production dispatch.",
            EvidenceFor(station, entry.Revision));
    }

    private static StationProductionExecutionGateResult Rejected(
        StationLifecycle station,
        long revision,
        string reason) =>
        new(
            Managed: true,
            Allowed: false,
            reason,
            EvidenceFor(station, revision));

    private static string EvidenceFor(StationLifecycle station, long revision) =>
        $"station-lifecycle:{station.Id.Value}:{revision}:{station.Mode}:{station.State}:"
        + $"{station.Readiness.InterlocksSatisfied}:{station.Readiness.Homed}:"
        + $"{station.Readiness.CriticalDevicesHealthy}:{station.Readiness.RecipeVerified}:"
        + $"{station.Readiness.CalibrationValid}:{station.Readiness.SafetyPermitGranted}";
}
