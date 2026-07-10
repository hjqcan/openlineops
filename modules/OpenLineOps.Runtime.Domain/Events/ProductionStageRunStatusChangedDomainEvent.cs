using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record ProductionStageRunStatusChangedDomainEvent(
    ProductionRunId RunId,
    string StageId,
    int Sequence,
    ProductionStageRunStatus FromStatus,
    ProductionStageRunStatus ToStatus,
    RuntimeSessionId? RuntimeSessionId,
    string Reason)
    : DomainEvent("ProductionRun.StageStatusChanged");
