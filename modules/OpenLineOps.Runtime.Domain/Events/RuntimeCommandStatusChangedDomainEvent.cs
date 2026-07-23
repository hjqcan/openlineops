using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record RuntimeCommandStatusChangedDomainEvent(
    RuntimeSessionId SessionId,
    RuntimeCommandId CommandId,
    ExecutionStatus FromStatus,
    ExecutionStatus ToStatus,
    string Reason)
    : DomainEvent("RuntimeCommand.StatusChanged");
