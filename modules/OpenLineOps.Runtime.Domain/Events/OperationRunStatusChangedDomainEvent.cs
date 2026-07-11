using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record OperationRunStatusChangedDomainEvent(
    ProductionRunId RunId,
    string OperationRunId,
    string OperationId,
    ExecutionStatus FromStatus,
    ExecutionStatus ToStatus,
    RuntimeSessionId? RuntimeSessionId,
    string Reason)
    : DomainEvent("ProductionRun.OperationStatusChanged");
