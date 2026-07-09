using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.SampleInspection.Domain.Identifiers;

namespace OpenLineOps.SampleInspection.Domain.Events;

public sealed record InspectionPlanCreatedDomainEvent(
    InspectionPlanId PlanId,
    string TargetDeviceId,
    DateTimeOffset CreatedAtUtc)
    : DomainEvent("inspection.plan.created");
