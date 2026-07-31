using OpenLineOps.Commissioning.Application.Security;
using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Infrastructure.Security;

public sealed class CommissioningRoleAccessPolicy : ICommissioningAccessPolicy
{
    private readonly Dictionary<string, IReadOnlySet<string>> _actorRoles;
    private readonly Dictionary<string, IReadOnlySet<CommissioningCapability>>
        _roleCapabilities;

    public CommissioningRoleAccessPolicy(
        IReadOnlyDictionary<string, IReadOnlySet<string>> actorRoles,
        IReadOnlyDictionary<string, IReadOnlySet<CommissioningCapability>> roleCapabilities)
    {
        ArgumentNullException.ThrowIfNull(actorRoles);
        ArgumentNullException.ThrowIfNull(roleCapabilities);
        _actorRoles = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var (actor, roles) in actorRoles)
        {
            if (string.IsNullOrWhiteSpace(actor)
                || !string.Equals(actor, actor.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Commissioning actor IDs must be non-empty canonical text.",
                    nameof(actorRoles));
            }

            ArgumentNullException.ThrowIfNull(roles);
            var snapshot = roles.ToHashSet(StringComparer.Ordinal);
            if (snapshot.Count == 0
                || snapshot.Any(static role =>
                    string.IsNullOrWhiteSpace(role)
                    || !string.Equals(role, role.Trim(), StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "Commissioning actor role grants must be non-empty canonical text.",
                    nameof(actorRoles));
            }

            _actorRoles.Add(actor, snapshot);
        }

        _roleCapabilities = new Dictionary<string, IReadOnlySet<CommissioningCapability>>(
            StringComparer.Ordinal);
        foreach (var (role, capabilities) in roleCapabilities)
        {
            if (string.IsNullOrWhiteSpace(role)
                || !string.Equals(role, role.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Commissioning role names must be non-empty canonical text.",
                    nameof(roleCapabilities));
            }

            ArgumentNullException.ThrowIfNull(capabilities);
            var snapshot = capabilities.ToHashSet();
            if (snapshot.Count == 0
                || snapshot.Any(static capability => !Enum.IsDefined(capability)))
            {
                throw new ArgumentException(
                    "Commissioning role capabilities must be non-empty and defined.",
                    nameof(roleCapabilities));
            }

            _roleCapabilities.Add(role, snapshot);
        }
    }

    public ValueTask<bool> CanStartAsync(
        string actorId,
        string authorizedRole,
        IReadOnlyCollection<CommissioningCapability> requestedCapabilities,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(actorId)
            || string.IsNullOrWhiteSpace(authorizedRole)
            || requestedCapabilities is null
            || requestedCapabilities.Count == 0)
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(
            _actorRoles.TryGetValue(actorId, out var actorRoles)
            && actorRoles.Contains(authorizedRole)
            && _roleCapabilities.TryGetValue(authorizedRole, out var allowed)
            && requestedCapabilities.All(capability =>
                Enum.IsDefined(capability) && allowed.Contains(capability)));
    }
}
