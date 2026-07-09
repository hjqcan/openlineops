using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Steps;

namespace OpenLineOps.Runtime.Domain.Events;

public sealed record RuntimeStepStatusChangedDomainEvent(
    RuntimeSessionId SessionId,
    RuntimeStepId StepId,
    RuntimeStepStatus FromStatus,
    RuntimeStepStatus ToStatus)
    : DomainEvent("RuntimeStep.StatusChanged");
