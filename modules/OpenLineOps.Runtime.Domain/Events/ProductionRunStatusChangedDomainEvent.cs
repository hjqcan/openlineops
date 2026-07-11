using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record ProductionRunStatusChangedDomainEvent(
    ProductionRunId RunId,
    ExecutionStatus FromStatus,
    ExecutionStatus ToStatus,
    string Reason)
    : DomainEvent("ProductionRun.StatusChanged");
