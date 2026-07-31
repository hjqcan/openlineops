using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Application.Stations;

public sealed class StationProductionExecutionGate(
    IStationLifecycleRepository repository,
    IStationControllerHandshakeRepository controllerHandshakeRepository,
    IStationAgentControlLeaseRepository agentControlLeaseRepository,
    IClock clock,
    StationControllerHandshakeOptions controllerHandshakeOptions)
    : IStationProductionExecutionGate
{
    public async ValueTask<StationProductionExecutionGateResult> EvaluateAsync(
        string stationSystemId,
        StationExecutionRecipeExpectation? expectedRecipe = null,
        CancellationToken cancellationToken = default)
    {
        var stationId = new StationId(stationSystemId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return new StationProductionExecutionGateResult(
                    Managed: false,
                    Allowed: false,
                    expectedRecipe is null
                        ? $"Station lifecycle {stationSystemId} is not enrolled."
                        : $"Station lifecycle {stationSystemId} is not enrolled for "
                          + $"released recipe {expectedRecipe.RecipeId}/"
                          + $"{expectedRecipe.RecipeVersion}.",
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

            var controllerHandshake = await controllerHandshakeRepository
                .GetByIdAsync(station.Id, cancellationToken)
                .ConfigureAwait(false);
            if (controllerHandshake is null)
            {
                return Rejected(
                    station,
                    entry.Revision,
                    "Station controller handshake has not been reported.");
            }

            var controllerReadiness =
                StationControllerHandshakeReadinessEvaluator.Evaluate(
                    controllerHandshake.State,
                    clock.UtcNow,
                    controllerHandshakeOptions);
            if (!controllerReadiness.Allowed)
            {
                return Rejected(
                    station,
                    entry.Revision,
                    controllerReadiness.Reason,
                    controllerHandshake);
            }

            var report = controllerHandshake.State.LatestReport;
            if (report.ObservedMode != station.Mode
                || report.ObservedState != station.State)
            {
                return Rejected(
                    station,
                    entry.Revision,
                    $"Controller observes {report.ObservedMode}/{report.ObservedState}, "
                    + $"expected lifecycle {station.Mode}/{station.State}.",
                    controllerHandshake);
            }

            if (expectedRecipe is not null
                && (!string.Equals(
                        report.ConfirmedRecipeId,
                        expectedRecipe.RecipeId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        report.ConfirmedRecipeVersion,
                        expectedRecipe.RecipeVersion,
                        StringComparison.Ordinal)))
            {
                return Rejected(
                    station,
                    entry.Revision,
                    $"Controller confirmed recipe {report.ConfirmedRecipeId}/"
                    + $"{report.ConfirmedRecipeVersion}, expected "
                    + $"{expectedRecipe.RecipeId}/{expectedRecipe.RecipeVersion}.",
                    controllerHandshake);
            }

            var controlLeaseObservation = await agentControlLeaseRepository
                .GetAsync(station.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!controlLeaseObservation.IsActive
                || controlLeaseObservation.Lease is not { } controlLease)
            {
                return Rejected(
                    station,
                    entry.Revision,
                    "Station Agent control lease is missing or expired.",
                    controllerHandshake);
            }

            if (!string.Equals(
                    report.OwnerAgentId,
                    controlLease.OwnerAgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    report.OwnerAgentInstanceId,
                    controlLease.OwnerInstanceId,
                    StringComparison.Ordinal)
                || report.AgentFencingToken != controlLease.FencingToken)
            {
                return Rejected(
                    station,
                    entry.Revision,
                    "Controller handshake owner boot or fencing token does not match "
                    + "the active Station Agent control lease.",
                    controllerHandshake,
                    controlLease);
            }

            var evidence = EvidenceFor(
                station,
                entry.Revision,
                controllerHandshake,
                controlLease);
            var authorizationRevision = RevisionFor(
                station,
                entry.Revision,
                controllerHandshake,
                controlLease);
            var confirmedLifecycle = await repository.GetByIdAsync(
                    stationId,
                    cancellationToken)
                .ConfigureAwait(false);
            var confirmedController = await controllerHandshakeRepository
                .GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            var confirmedControlLease = await agentControlLeaseRepository
                .ValidateGenerationAsync(
                    stationId,
                    controlLease.OwnerAgentId,
                    controlLease.OwnerInstanceId,
                    controlLease.FencingToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (confirmedLifecycle is not null
                && confirmedController is not null
                && confirmedControlLease.IsValid
                && confirmedControlLease.Observation.Lease is { } latestControlLease
                && Equals(latestControlLease, controlLease)
                && confirmedLifecycle.Revision == entry.Revision
                && confirmedController.Revision == controllerHandshake.Revision
                && string.Equals(
                    RevisionFor(
                        confirmedLifecycle.Station,
                        confirmedLifecycle.Revision,
                        confirmedController,
                        latestControlLease),
                    authorizationRevision,
                    StringComparison.Ordinal)
                && string.Equals(
                    EvidenceFor(
                        confirmedLifecycle.Station,
                        confirmedLifecycle.Revision,
                        confirmedController,
                        latestControlLease),
                    evidence,
                    StringComparison.Ordinal))
            {
                var validUntilUtc = Earlier(
                    report.ReceivedAtUtc.Add(controllerHandshakeOptions.TimeToLive),
                    latestControlLease.ExpiresAtUtc);
                if (validUntilUtc <= clock.UtcNow)
                {
                    return Rejected(
                        station,
                        entry.Revision,
                        "Station execution authority expired during dispatch authorization.",
                        controllerHandshake,
                        latestControlLease);
                }

                return new StationProductionExecutionGateResult(
                    Managed: true,
                    Allowed: true,
                    "Station lifecycle permits production dispatch.",
                    evidence,
                    validUntilUtc,
                    new StationAgentControlLeaseDispatchAuthority(
                        latestControlLease.OwnerAgentId,
                        latestControlLease.OwnerInstanceId,
                        latestControlLease.FencingToken,
                        latestControlLease.ExpiresAtUtc),
                    authorizationRevision);
            }
        }

        return new StationProductionExecutionGateResult(
            Managed: true,
            Allowed: false,
            "Station lifecycle, controller handshake, or Agent control lease changed "
            + "while production dispatch was authorized.",
            Evidence: null);
    }

    private static StationProductionExecutionGateResult Rejected(
        StationLifecycle station,
        long revision,
        string reason,
        StationControllerHandshakePersistenceEntry? controllerHandshake = null,
        StationAgentControlLease? controlLease = null) =>
        new(
            Managed: true,
            Allowed: false,
            reason,
            EvidenceFor(station, revision, controllerHandshake, controlLease));

    private static string EvidenceFor(
        StationLifecycle station,
        long revision,
        StationControllerHandshakePersistenceEntry? controllerHandshake = null,
        StationAgentControlLease? controlLease = null)
    {
        var lifecycle =
            $"station-lifecycle:{station.Id.Value}:{revision}:{station.Mode}:{station.State}:"
        + $"{station.Readiness.InterlocksSatisfied}:{station.Readiness.Homed}:"
        + $"{station.Readiness.CriticalDevicesHealthy}:{station.Readiness.RecipeVerified}:"
        + $"{station.Readiness.CalibrationValid}:{station.Readiness.SafetyPermitGranted}";
        if (controllerHandshake is null)
        {
            return lifecycle;
        }

        var report = controllerHandshake.State.LatestReport;
        var controllerEvidence = lifecycle
            + $"|controller-handshake:{controllerHandshake.Revision}:"
            + $"{Uri.EscapeDataString(report.OwnerAgentId)}:"
            + $"{Uri.EscapeDataString(report.OwnerAgentInstanceId)}:"
            + $"{report.AgentFencingToken}:"
            + $"{Uri.EscapeDataString(report.ControllerSessionId)}:"
            + $"{report.HeartbeatSequence}:"
            + $"{report.CommandSequence}:{report.AcknowledgedCommandSequence}:"
            + $"{Encode(report.CommandId)}:{report.CommandFencingToken}:"
            + $"{report.ObservedMode}:{report.ObservedState}:{report.StateSequence}:"
            + $"{report.Busy}:{report.Completed}:{report.Error}:{Encode(report.ErrorCode)}:"
            + $"{report.RecipeConfirmed}:"
            + $"{Encode(report.ConfirmedRecipeId)}:"
            + $"{Encode(report.ConfirmedRecipeVersion)}:"
            + $"{report.SafetyPermitGranted}:"
            + $"{controllerHandshake.State.RecoveryRequired}:"
            + $"{controllerHandshake.State.RecoveryEpoch}:"
            + $"{controllerHandshake.State.OperationalEpoch}:"
            + $"{report.ReceivedAtUtc:O}";
        return controlLease is null
            ? controllerEvidence
            : controllerEvidence
              + $"|agent-control-lease:{Uri.EscapeDataString(controlLease.OwnerAgentId)}:"
              + $"{Uri.EscapeDataString(controlLease.OwnerInstanceId)}:"
              + $"{controlLease.FencingToken}:{controlLease.AcquiredAtUtc:O}:"
              + $"{controlLease.RenewedAtUtc:O}:{controlLease.ExpiresAtUtc:O}";
    }

    private static DateTimeOffset Earlier(
        DateTimeOffset left,
        DateTimeOffset right) => left <= right ? left : right;

    private static string RevisionFor(
        StationLifecycle station,
        long lifecycleRevision,
        StationControllerHandshakePersistenceEntry controllerHandshake,
        StationAgentControlLease controlLease)
    {
        var report = controllerHandshake.State.LatestReport;
        return $"station-gate-revision:v1:{station.Id.Value}:{lifecycleRevision}:"
            + $"{station.Mode}:{station.State}:"
            + $"{station.Readiness.InterlocksSatisfied}:{station.Readiness.Homed}:"
            + $"{station.Readiness.CriticalDevicesHealthy}:"
            + $"{station.Readiness.RecipeVerified}:{station.Readiness.CalibrationValid}:"
            + $"{station.Readiness.SafetyPermitGranted}:"
            + $"{controllerHandshake.State.OperationalEpoch}:"
            + $"{controllerHandshake.State.RecoveryRequired}:"
            + $"{controllerHandshake.State.RecoveryEpoch}:"
            + $"{Uri.EscapeDataString(report.OwnerAgentId)}:"
            + $"{Uri.EscapeDataString(report.OwnerAgentInstanceId)}:"
            + $"{report.AgentFencingToken}:"
            + $"{Uri.EscapeDataString(report.ControllerSessionId)}:"
            + $"{report.CommandSequence}:{report.AcknowledgedCommandSequence}:"
            + $"{Encode(report.CommandId)}:{report.CommandFencingToken}:"
            + $"{report.ObservedMode}:{report.ObservedState}:{report.StateSequence}:"
            + $"{report.Busy}:{report.Completed}:{report.Error}:{Encode(report.ErrorCode)}:"
            + $"{report.RecipeConfirmed}:{Encode(report.ConfirmedRecipeId)}:"
            + $"{Encode(report.ConfirmedRecipeVersion)}:{report.SafetyPermitGranted}:"
            + $"{Uri.EscapeDataString(controlLease.OwnerAgentId)}:"
            + $"{Uri.EscapeDataString(controlLease.OwnerInstanceId)}:"
            + $"{controlLease.FencingToken}";
    }

    private static string Encode(string? value) =>
        value is null ? "none" : Uri.EscapeDataString(value);
}
