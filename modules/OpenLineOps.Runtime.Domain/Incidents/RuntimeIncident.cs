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
        Code = Required(code, nameof(code));
        Message = Required(message, nameof(message));
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

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
    }
}
