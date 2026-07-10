using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record ProductionRunStatusChangedDomainEvent(
    ProductionRunId RunId,
    ProductionRunStatus FromStatus,
    ProductionRunStatus ToStatus,
    string Reason)
    : DomainEvent("ProductionRun.StatusChanged");
