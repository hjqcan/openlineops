namespace OpenLineOps.Runtime.Api.Models;

public sealed record RuntimeIncidentResponse(
    Guid IncidentId,
    string Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc);
