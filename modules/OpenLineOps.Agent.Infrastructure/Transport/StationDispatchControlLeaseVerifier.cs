using OpenLineOps.Agent.Application.StationController;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed class StationDispatchControlLeaseVerifier(
    string stationSystemId,
    StationAgentProcessIdentity identity,
    StationAgentControlLeaseState leaseState,
    IStationControllerCoordinatorClient coordinator,
    IClock clock,
    TimeSpan requestTimeout) : IStationDispatchControlLeaseVerifier
{
    private readonly string _stationSystemId = Required(
        stationSystemId,
        nameof(stationSystemId));
    private readonly StationAgentProcessIdentity _identity =
        identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly StationAgentControlLeaseState _leaseState =
        leaseState ?? throw new ArgumentNullException(nameof(leaseState));
    private readonly IStationControllerCoordinatorClient _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly TimeSpan _requestTimeout = requestTimeout
        is { } timeout
        && timeout >= TimeSpan.FromMilliseconds(100)
        && timeout <= TimeSpan.FromMinutes(2)
            ? timeout
            : throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "Control lease verification timeout must be between 100 ms and 2 minutes.");

    public async ValueTask<StationDispatchControlLeaseVerificationResult>
        ValidateCurrentAsync(
            StationDispatchControlLeaseExpectation expectation,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        cancellationToken.ThrowIfCancellationRequested();
        var authority = expectation.Authority;
        if (!string.Equals(
                expectation.DispatchAgentId,
                _identity.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                expectation.DispatchStationId,
                _identity.StationId,
                StringComparison.Ordinal)
            || !string.Equals(
                expectation.StationSystemId,
                _stationSystemId,
                StringComparison.Ordinal)
            || !string.Equals(
                authority.OwnerAgentId,
                _identity.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                authority.OwnerInstanceId,
                _identity.OwnerInstanceId,
                StringComparison.Ordinal))
        {
            return StationDispatchControlLeaseVerificationResult.Reject(
                "Dispatch control lease does not target this Agent boot and Station.");
        }

        var nowUtc = UtcNow();
        if (authority.FencingToken < 1
            || authority.ExpiresAtUtc == default
            || authority.ExpiresAtUtc.Offset != TimeSpan.Zero
            || authority.ExpiresAtUtc <= nowUtc)
        {
            return StationDispatchControlLeaseVerificationResult.Reject(
                "Dispatch control lease authority is invalid or expired.");
        }

        if (!_leaseState.TryGetActive(out var activeLease)
            || activeLease is null)
        {
            return StationDispatchControlLeaseVerificationResult.Unavailable(
                "This Agent boot has no active proof-bearing Station control lease.");
        }

        if (authority.FencingToken != activeLease.FencingToken
            || authority.ExpiresAtUtc > activeLease.ExpiresAtUtc
            || !string.Equals(
                activeLease.OwnerInstanceId,
                _identity.OwnerInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                activeLease.LeaseHandle,
                _identity.LeaseHandle,
                StringComparison.Ordinal))
        {
            return StationDispatchControlLeaseVerificationResult.Reject(
                "Dispatch control lease generation, expiry, or boot proof is stale.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        DateTimeOffset renewedUntilUtc;
        try
        {
            renewedUntilUtc = await _coordinator.RenewLeaseAsync(
                    _identity,
                    activeLease,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (StationAgentControlLeaseRejectedException)
        {
            _leaseState.Clear(activeLease);
            return StationDispatchControlLeaseVerificationResult.Reject(
                "Coordinator rejected this Agent boot's Station control lease proof.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StationDispatchControlLeaseVerificationResult.Unavailable(
                "Coordinator control lease proof verification timed out.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or InvalidDataException)
        {
            return StationDispatchControlLeaseVerificationResult.Unavailable(
                "Coordinator control lease proof verification is temporarily unavailable.");
        }

        if (renewedUntilUtc == default
            || renewedUntilUtc.Offset != TimeSpan.Zero
            || renewedUntilUtc <= UtcNow())
        {
            _leaseState.Clear(activeLease);
            return StationDispatchControlLeaseVerificationResult.Reject(
                "Coordinator returned an expired Station control lease renewal.");
        }

        try
        {
            _leaseState.ActivateOrRenew(activeLease.Renewed(renewedUntilUtc));
        }
        catch (StationAgentControlLeaseRejectedException)
        {
            return StationDispatchControlLeaseVerificationResult.Reject(
                "Station control lease changed while dispatch proof was verified.");
        }

        return StationDispatchControlLeaseVerificationResult.Allow();
    }

    private DateTimeOffset UtcNow()
    {
        var nowUtc = _clock.UtcNow;
        return nowUtc == default || nowUtc.Offset != TimeSpan.Zero
            ? throw new InvalidOperationException(
                "Station control lease verifier clock must return non-default UTC.")
            : nowUtc;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"{parameterName} must be canonical text.",
                parameterName)
            : value;
}
