using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Incidents;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record RuntimeIncidentRecordedDomainEvent(
    RuntimeSessionId SessionId,
    RuntimeIncidentId IncidentId,
    RuntimeIncidentSeverity Severity,
    string Code)
    : DomainEvent("RuntimeIncident.Recorded");
