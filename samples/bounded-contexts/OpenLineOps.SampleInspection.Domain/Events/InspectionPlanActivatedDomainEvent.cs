using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.SampleInspection.Domain.Identifiers;

namespace OpenLineOps.SampleInspection.Domain.Events;

public sealed record InspectionPlanActivatedDomainEvent(
    InspectionPlanId PlanId,
    DateTimeOffset ActivatedAtUtc)
    : DomainEvent("inspection.plan.activated");
