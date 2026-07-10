using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;

namespace OpenLineOps.Runtime.Domain.Steps;

public sealed class RuntimeStep : Entity<RuntimeStepId>
{
    private RuntimeStep(
        RuntimeStepId id,
        RuntimeNodeId nodeId,
        string displayName,
        DateTimeOffset startedAtUtc,
        RuntimeActionId? actionId,
        RuntimeStepId? parentStepId,
        int? dynamicSequence)
        : base(id)
    {
        NodeId = nodeId;
        ActionId = actionId ?? new RuntimeActionId($"{nodeId.Value}:action:1");
        ParentStepId = parentStepId;
        DynamicSequence = dynamicSequence;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? nodeId.Value
            : displayName.Trim();
        StartedAtUtc = startedAtUtc;
        Status = RuntimeStepStatus.Running;
    }

    public RuntimeNodeId NodeId { get; }

    public RuntimeActionId ActionId { get; }

    public RuntimeStepId? ParentStepId { get; }

    public int? DynamicSequence { get; }

    public string DisplayName { get; }

    public RuntimeStepStatus Status { get; private set; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? FailureReason { get; private set; }

    public bool IsTerminal => Status is RuntimeStepStatus.Completed
        or RuntimeStepStatus.Failed
        or RuntimeStepStatus.Skipped
        or RuntimeStepStatus.Canceled;

    public static RuntimeStep Start(
        RuntimeStepId id,
        RuntimeNodeId nodeId,
        string displayName,
        DateTimeOffset startedAtUtc,
        RuntimeActionId? actionId = null,
        RuntimeStepId? parentStepId = null,
        int? dynamicSequence = null)
    {
        ValidateDynamicIdentity(parentStepId, dynamicSequence);
        return new RuntimeStep(
            id,
            nodeId,
            displayName,
            startedAtUtc,
            actionId,
            parentStepId,
            dynamicSequence);
    }

    public static RuntimeStep Restore(
        RuntimeStepId id,
        RuntimeNodeId nodeId,
        string displayName,
        RuntimeStepStatus status,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? failureReason,
        RuntimeActionId? actionId = null,
        RuntimeStepId? parentStepId = null,
        int? dynamicSequence = null)
    {
        ValidateDynamicIdentity(parentStepId, dynamicSequence);
        var step = new RuntimeStep(
            id,
            nodeId,
            displayName,
            startedAtUtc,
            actionId,
            parentStepId,
            dynamicSequence)
        {
            Status = status,
            CompletedAtUtc = completedAtUtc,
            FailureReason = NormalizeOptional(failureReason)
        };

        return step;
    }

    internal RuntimeOperationResult Complete(DateTimeOffset completedAtUtc)
    {
        return TransitionTo(RuntimeStepStatus.Completed, completedAtUtc);
    }

    internal RuntimeOperationResult Fail(string reason, DateTimeOffset failedAtUtc)
    {
        FailureReason = string.IsNullOrWhiteSpace(reason)
            ? "Step failed."
            : reason.Trim();

        return TransitionTo(RuntimeStepStatus.Failed, failedAtUtc);
    }

    internal RuntimeOperationResult Cancel(DateTimeOffset canceledAtUtc)
    {
        return TransitionTo(RuntimeStepStatus.Canceled, canceledAtUtc);
    }

    private RuntimeOperationResult TransitionTo(RuntimeStepStatus target, DateTimeOffset utcNow)
    {
        if (Status == target)
        {
            return RuntimeOperationResult.Accepted();
        }

        if (IsTerminal)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.StepAlreadyTerminal",
                $"Step {Id} is already terminal.");
        }

        Status = target;
        CompletedAtUtc = utcNow;

        return RuntimeOperationResult.Accepted();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static void ValidateDynamicIdentity(
        RuntimeStepId? parentStepId,
        int? dynamicSequence)
    {
        if (parentStepId is null && dynamicSequence is not null)
        {
            throw new ArgumentException(
                "DynamicSequence requires a parent runtime step.",
                nameof(dynamicSequence));
        }

        if (parentStepId is not null && dynamicSequence is not > 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dynamicSequence),
                dynamicSequence,
                "A child runtime step must declare a positive dynamic sequence.");
        }
    }
}
