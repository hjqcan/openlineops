using OpenLineOps.Traceability.Domain.Identifiers;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed record TraceIncidentRecord
{
    public TraceIncidentRecord(
        Guid runtimeIncidentId,
        TraceIncidentSeverity severity,
        string code,
        string message,
        DateTimeOffset occurredAtUtc)
    {
        RuntimeIncidentId = TraceabilityIdGuard.NotEmpty(runtimeIncidentId, nameof(runtimeIncidentId));
        Severity = severity;
        Code = TraceabilityIdGuard.NotBlank(code, nameof(code));
        Message = TraceabilityIdGuard.NotBlank(message, nameof(message));
        OccurredAtUtc = occurredAtUtc == default
            ? throw new ArgumentException("Incident timestamp is required.", nameof(occurredAtUtc))
            : occurredAtUtc;
    }

    public Guid RuntimeIncidentId { get; }

    public TraceIncidentSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}
