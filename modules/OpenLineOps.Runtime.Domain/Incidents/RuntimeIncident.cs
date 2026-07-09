using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Incidents;

public sealed class RuntimeIncident : Entity<RuntimeIncidentId>
{
    private RuntimeIncident(
        RuntimeIncidentId id,
        RuntimeIncidentSeverity severity,
        string code,
        string message,
        DateTimeOffset occurredAtUtc)
        : base(id)
    {
        Severity = severity;
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Incident code cannot be empty.", nameof(code))
            : code.Trim();
        Message = string.IsNullOrWhiteSpace(message)
            ? throw new ArgumentException("Incident message cannot be empty.", nameof(message))
            : message.Trim();
        OccurredAtUtc = occurredAtUtc;
    }

    public RuntimeIncidentSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public static RuntimeIncident Record(
        RuntimeIncidentId id,
        RuntimeIncidentSeverity severity,
        string code,
        string message,
        DateTimeOffset occurredAtUtc)
    {
        return new RuntimeIncident(id, severity, code, message, occurredAtUtc);
    }
}
