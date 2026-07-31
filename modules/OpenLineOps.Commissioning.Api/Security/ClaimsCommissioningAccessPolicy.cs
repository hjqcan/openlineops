using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OpenLineOps.Commissioning.Api.DependencyInjection;
using OpenLineOps.Commissioning.Application.Security;
using OpenLineOps.Commissioning.Domain.Sessions;

namespace OpenLineOps.Commissioning.Api.Security;

public sealed class ClaimsCommissioningAccessPolicy(
    IHttpContextAccessor httpContextAccessor,
    IOptions<CommissioningAuthorizationOptions> options)
    : ICommissioningAccessPolicy
{
    private readonly Dictionary<string, IReadOnlySet<CommissioningCapability>>
        _roleCapabilities = BuildRoleCapabilities(options.Value);

    public ValueTask<bool> CanStartAsync(
        string actorId,
        string authorizedRole,
        IReadOnlyCollection<CommissioningCapability> requestedCapabilities,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var principal = httpContextAccessor.HttpContext?.User;
        var authenticatedActor = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return ValueTask.FromResult(
            principal?.Identity?.IsAuthenticated == true
            && string.Equals(authenticatedActor, actorId, StringComparison.Ordinal)
            && principal.IsInRole(authorizedRole)
            && requestedCapabilities is { Count: > 0 }
            && _roleCapabilities.TryGetValue(authorizedRole, out var allowed)
            && requestedCapabilities.All(allowed.Contains));
    }

    private static Dictionary<string, IReadOnlySet<CommissioningCapability>>
        BuildRoleCapabilities(CommissioningAuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var validation = new CommissioningAuthorizationOptionsValidator()
            .Validate(name: null, options);
        if (validation.Failed)
        {
            throw new InvalidOperationException(
                string.Join("; ", validation.Failures));
        }

        return options.RoleCapabilities.ToDictionary(
            static grant => grant.Key,
            static grant => (IReadOnlySet<CommissioningCapability>)grant.Value
                .Select(value =>
                {
                    _ = CommissioningAuthorizationOptionsValidator.TryParseCapability(
                        value,
                        out var capability);
                    return capability;
                })
                .ToHashSet(),
            StringComparer.Ordinal);
    }
}
