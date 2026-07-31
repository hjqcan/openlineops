using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Integration.Application.Outbox;

namespace OpenLineOps.Integration.Api.Security;

public sealed class ClaimsIntegrationReplayAuthorizer(
    IHttpContextAccessor httpContextAccessor) : IIntegrationReplayAuthorizer
{
    public ValueTask<bool> IsAuthorizedAsync(
        ManualOutboxReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var principal = httpContextAccessor.HttpContext?.User;
        var actorId = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return ValueTask.FromResult(
            principal?.Identity?.IsAuthenticated == true
            && principal.IsInRole(OpenLineOpsApiSecurity.EngineeringRole)
            && string.Equals(actorId, request.ActorId, StringComparison.Ordinal));
    }
}
