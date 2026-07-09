using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record RuntimeCommandStatusChangedDomainEvent(
    RuntimeSessionId SessionId,
    RuntimeCommandId CommandId,
    RuntimeCommandStatus FromStatus,
    RuntimeCommandStatus ToStatus,
    string Reason)
    : DomainEvent("RuntimeCommand.StatusChanged");
