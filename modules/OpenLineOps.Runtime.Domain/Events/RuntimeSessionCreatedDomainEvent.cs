using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record RuntimeSessionCreatedDomainEvent(RuntimeSessionId SessionId)
    : DomainEvent("RuntimeSession.Created");
