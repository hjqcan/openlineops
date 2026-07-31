using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Application.Security;

public interface ICommissioningAccessPolicy
{
    ValueTask<bool> CanStartAsync(
        string actorId,
        string authorizedRole,
        IReadOnlyCollection<CommissioningCapability> requestedCapabilities,
        CancellationToken cancellationToken = default);
}
