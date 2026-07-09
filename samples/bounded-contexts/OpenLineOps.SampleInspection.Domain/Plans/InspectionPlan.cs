using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.SampleInspection.Domain.Events;
using OpenLineOps.SampleInspection.Domain.Identifiers;
using OpenLineOps.SampleInspection.Domain.Operations;

namespace OpenLineOps.SampleInspection.Domain.Plans;

public sealed class InspectionPlan : AggregateRoot<InspectionPlanId>
{
    private InspectionPlan()
        : base(new InspectionPlanId("__ef_materialization__"))
    {
        DisplayName = string.Empty;
        TargetDeviceId = string.Empty;
    }

    private InspectionPlan(
        InspectionPlanId id,
        string displayName,
        string targetDeviceId,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        DisplayName = RequiredText(displayName, nameof(displayName));
        TargetDeviceId = RequiredText(targetDeviceId, nameof(targetDeviceId));
        CreatedAtUtc = createdAtUtc;
        Status = InspectionPlanStatus.Draft;
    }

    public string DisplayName { get; private set; }

    public string TargetDeviceId { get; private set; }

    public InspectionPlanStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? ActivatedAtUtc { get; private set; }

    public bool IsActive => Status == InspectionPlanStatus.Active;

    public static InspectionPlan Create(
        InspectionPlanId id,
        string displayName,
        string targetDeviceId,
        DateTimeOffset createdAtUtc)
    {
        var plan = new InspectionPlan(id, displayName, targetDeviceId, createdAtUtc);
        plan.RaiseDomainEvent(new InspectionPlanCreatedDomainEvent(id, plan.TargetDeviceId, createdAtUtc));

        return plan;
    }

    public InspectionOperationResult Rename(string displayName)
    {
        if (Status == InspectionPlanStatus.Retired)
        {
            return InspectionOperationResult.Rejected(
                "Inspection.PlanRetired",
                $"Inspection plan {Id} cannot be changed after retirement.");
        }

        DisplayName = RequiredText(displayName, nameof(displayName));

        return InspectionOperationResult.Accepted("Inspection plan renamed.");
    }

    public InspectionOperationResult Activate(DateTimeOffset activatedAtUtc)
    {
        if (Status == InspectionPlanStatus.Retired)
        {
            return InspectionOperationResult.Rejected(
                "Inspection.PlanRetired",
                $"Inspection plan {Id} cannot be activated after retirement.");
        }

        if (Status == InspectionPlanStatus.Active)
        {
            return InspectionOperationResult.Accepted("Inspection plan is already active.");
        }

        Status = InspectionPlanStatus.Active;
        ActivatedAtUtc = activatedAtUtc;
        RaiseDomainEvent(new InspectionPlanActivatedDomainEvent(Id, activatedAtUtc));

        return InspectionOperationResult.Accepted("Inspection plan activated.");
    }

    public InspectionOperationResult Retire()
    {
        if (Status == InspectionPlanStatus.Retired)
        {
            return InspectionOperationResult.Accepted("Inspection plan is already retired.");
        }

        Status = InspectionPlanStatus.Retired;

        return InspectionOperationResult.Accepted("Inspection plan retired.");
    }

    private static string RequiredText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Text values cannot be blank.", parameterName);
        }

        return value.Trim();
    }
}
