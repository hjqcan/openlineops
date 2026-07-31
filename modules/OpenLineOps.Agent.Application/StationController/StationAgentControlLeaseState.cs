namespace OpenLineOps.Agent.Application.StationController;

public sealed class StationAgentControlLeaseState
{
    private readonly object _gate = new();
    private StationAgentControlLeaseGrant? _activeLease;

    public StationAgentControlLeaseState(StationAgentProcessIdentity identity)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public StationAgentProcessIdentity Identity { get; }

    public void ActivateOrRenew(StationAgentControlLeaseGrant lease)
    {
        ValidateIdentity(lease);
        lock (_gate)
        {
            if (_activeLease is { } current)
            {
                if (lease.FencingToken < current.FencingToken)
                {
                    throw new StationAgentControlLeaseRejectedException(
                        "Active Station control lease fencing token cannot regress.");
                }

                if (lease.FencingToken == current.FencingToken
                    && lease.ExpiresAtUtc < current.ExpiresAtUtc)
                {
                    throw new StationAgentControlLeaseRejectedException(
                        "Active Station control lease expiry cannot regress within "
                        + "one fencing generation.");
                }
            }

            _activeLease = lease;
        }
    }

    public bool TryGetActive(out StationAgentControlLeaseGrant? lease)
    {
        lock (_gate)
        {
            lease = _activeLease;
            return lease is not null;
        }
    }

    public void Clear(StationAgentControlLeaseGrant lease)
    {
        ValidateIdentity(lease);
        lock (_gate)
        {
            if (_activeLease is { } current
                && current.FencingToken == lease.FencingToken)
            {
                _activeLease = null;
            }
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return _activeLease is { } lease
                ? $"StationAgentControlLeaseState {{ StationId = {Identity.StationId}, "
                    + $"OwnerInstanceId = {Identity.OwnerInstanceId}, "
                    + $"FencingToken = {lease.FencingToken}, LeaseHandle = [REDACTED] }}"
                : $"StationAgentControlLeaseState {{ StationId = {Identity.StationId}, "
                    + "Active = false, LeaseHandle = [REDACTED] }";
        }
    }

    private void ValidateIdentity(StationAgentControlLeaseGrant lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(
                lease.StationId,
                Identity.StationId,
                StringComparison.Ordinal)
            || !string.Equals(
                lease.OwnerInstanceId,
                Identity.OwnerInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                lease.LeaseHandle,
                Identity.LeaseHandle,
                StringComparison.Ordinal))
        {
            throw new StationAgentControlLeaseRejectedException(
                "Station control lease state rejected a different process identity.");
        }
    }
}
