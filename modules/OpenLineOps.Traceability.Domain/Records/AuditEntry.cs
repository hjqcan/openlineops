using OpenLineOps.Traceability.Domain.Identifiers;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed record AuditEntry
{
    public AuditEntry(
        AuditEntryId id,
        ActorId actorId,
        string action,
        string? detail,
        DateTimeOffset occurredAtUtc)
    {
        Id = id;
        ActorId = actorId;
        Action = TraceabilityIdGuard.NotBlank(action, nameof(action));
        Detail = TraceabilityIdGuard.OptionalText(detail);
        OccurredAtUtc = occurredAtUtc;
    }

    public AuditEntryId Id { get; }

    public ActorId ActorId { get; }

    public string Action { get; }

    public string? Detail { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}
