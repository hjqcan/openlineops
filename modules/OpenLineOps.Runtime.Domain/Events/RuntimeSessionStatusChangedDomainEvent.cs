using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record RuntimeSessionStatusChangedDomainEvent(
    RuntimeSessionId SessionId,
    RuntimeSessionStatus FromStatus,
    RuntimeSessionStatus ToStatus,
    string Reason)
    : DomainEvent("RuntimeSession.StatusChanged");
