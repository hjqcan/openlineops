using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Targets;

namespace OpenLineOps.Runtime.Domain.Steps;

public sealed class RuntimeStep : Entity<RuntimeStepId>
{
    private RuntimeStep(
        RuntimeStepId id,
        RuntimeNodeId nodeId,
        string displayName,
        DateTimeOffset startedAtUtc,
        RuntimeActionId actionId,
        RuntimeTargetReference target)
        : base(id)
    {
        NodeId = nodeId;
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        DisplayName = Required(displayName, nameof(displayName));
        StartedAtUtc = startedAtUtc;
        Status = RuntimeStepStatus.Running;
    }

    public RuntimeNodeId NodeId { get; }

    public RuntimeActionId ActionId { get; }

    public RuntimeTargetReference Target { get; }

    public string TargetKind => Target.Kind;

    public string TargetId => Target.TargetId;

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
        RuntimeActionId actionId,
        RuntimeTargetReference target)
    {
        return new RuntimeStep(
            id,
            nodeId,
            displayName,
            startedAtUtc,
            actionId,
            target);
    }

    public static RuntimeStep Restore(
        RuntimeStepId id,
        RuntimeNodeId nodeId,
        string displayName,
        RuntimeStepStatus status,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? failureReason,
        RuntimeActionId actionId,
        RuntimeTargetReference target)
    {
        var step = new RuntimeStep(
            id,
            nodeId,
            displayName,
            startedAtUtc,
            actionId,
            target)
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
        var canonicalReason = Required(reason, nameof(reason));
        if (Status == RuntimeStepStatus.Failed)
        {
            return CompletedAtUtc == failedAtUtc
                   && string.Equals(FailureReason, canonicalReason, StringComparison.Ordinal)
                ? RuntimeOperationResult.Accepted()
                : RuntimeOperationResult.Rejected(
                    "Runtime.StepEvidenceConflict",
                    $"Step {Id} was replayed with different terminal evidence.");
        }

        var result = TransitionTo(RuntimeStepStatus.Failed, failedAtUtc);
        if (result.Succeeded)
        {
            FailureReason = canonicalReason;
        }

        return result;
    }

    internal RuntimeOperationResult Cancel(DateTimeOffset canceledAtUtc)
    {
        return TransitionTo(RuntimeStepStatus.Canceled, canceledAtUtc);
    }

    private RuntimeOperationResult TransitionTo(RuntimeStepStatus target, DateTimeOffset utcNow)
    {
        if (Status == target)
        {
            return CompletedAtUtc == utcNow
                ? RuntimeOperationResult.Accepted()
                : RuntimeOperationResult.Rejected(
                    "Runtime.StepEvidenceConflict",
                    $"Step {Id} was replayed with a different completion timestamp.");
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
        return value is null ? null : Required(value, nameof(value));
    }

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
    }

}
