using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Commissioning.Api.Models;
using OpenLineOps.Commissioning.Application.Contracts;

namespace OpenLineOps.Commissioning.Api.Mapping;

internal static class CommissioningApiMapper
{
    public static CommissioningSessionResponse ToResponse(
        CommissioningSessionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return new CommissioningSessionResponse(
            details.SessionId,
            details.Revision,
            details.StationId,
            details.RequestedBy,
            details.AuthorizedRole,
            details.LeaseId,
            details.FencingToken,
            details.StartedAtUtc,
            details.ExpiresAtUtc,
            details.Status.ToString(),
            details.RecoveryReason,
            details.Capabilities
                .OrderBy(static capability => capability)
                .Select(static capability => capability.ToString())
                .ToArray(),
            details.AuditTrail
                .OrderBy(static entry => entry.Sequence)
                .Select(static entry => new CommissioningAuditEntryResponse(
                    entry.Sequence,
                    entry.Kind.ToString(),
                    entry.ActorId,
                    entry.OccurredAtUtc,
                    entry.SubjectId,
                    entry.Reason,
                    entry.FencingToken))
                .ToArray());
    }

    public static ObjectResult ToProblem(ApplicationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = error.Code.StartsWith("NotFound.", StringComparison.Ordinal)
            ? StatusCodes.Status404NotFound
            : error.Code.StartsWith("Validation.", StringComparison.Ordinal)
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status409Conflict;
        return new ObjectResult(new ProblemDetails
        {
            Status = status,
            Title = error.Code,
            Detail = error.Message
        })
        {
            StatusCode = status
        };
    }

    public static ProblemDetails Problem(
        int status,
        string title,
        string detail) =>
        new()
        {
            Status = status,
            Title = title,
            Detail = detail
        };
}
