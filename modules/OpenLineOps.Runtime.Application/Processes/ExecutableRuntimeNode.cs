using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Targets;

namespace OpenLineOps.Runtime.Application.Processes;

public sealed record ExecutableRuntimeNode(
    RuntimeNodeId NodeId,
    string DisplayName,
    RuntimeCapabilityId TargetCapability,
    string CommandName,
    TimeSpan Timeout,
    string? InputPayload,
    RuntimeActionId ActionId,
    RuntimeTargetReference Target)
{
    public ExecutableRuntimeActionPolicy? OperationalPolicy { get; init; }

    public int RetryLimit { get; init; }

    public bool IsValid => Timeout > TimeSpan.Zero
        && RetryLimit is >= 0 and <= 10
        && ActionId is not null
        && Target is not null
        && !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(CommandName)
        && IsOperationalPolicyValid();

    private bool IsOperationalPolicyValid()
    {
        if (OperationalPolicy is null)
        {
            return RetryLimit == 0;
        }

        var policy = OperationalPolicy;
        return Enum.IsDefined(policy.IdempotencyClass)
            && Enum.IsDefined(policy.RecoveryPolicy)
            && Enum.IsDefined(policy.FailurePolicy)
            && policy.ResourceLocks is not null
            && policy.EvidenceRequirements is not null
            && policy.AllowedStationModes is { Count: > 0 }
            && (policy.FailurePolicy == RuntimeActionFailurePolicy.Retry)
                == (RetryLimit > 0)
            && (policy.FailurePolicy != RuntimeActionFailurePolicy.Retry
                || policy.IdempotencyClass == RuntimeActionIdempotencyClass.Idempotent)
            && (policy.RecoveryPolicy != RuntimeActionRecoveryPolicy.AutomaticReplay
                || policy.IdempotencyClass == RuntimeActionIdempotencyClass.Idempotent)
            && (policy.IdempotencyClass != RuntimeActionIdempotencyClass.NonIdempotent
                || policy.RecoveryPolicy is RuntimeActionRecoveryPolicy.ManualAuthorization
                    or RuntimeActionRecoveryPolicy.NeverReplay)
            && policy.ResourceLocks.All(resource =>
                resource is not null
                && !string.IsNullOrWhiteSpace(resource.ResourceId)
                && string.Equals(
                    resource.ResourceId,
                    resource.ResourceId.Trim(),
                    StringComparison.Ordinal)
                && Enum.IsDefined(resource.Mode))
            && policy.ResourceLocks
                .Select(resource => resource.ResourceId)
                .Distinct(StringComparer.Ordinal)
                .Count() == policy.ResourceLocks.Count
            && policy.EvidenceRequirements.All(evidence =>
                evidence is not null
                && !string.IsNullOrWhiteSpace(evidence.EvidenceKind)
                && string.Equals(
                    evidence.EvidenceKind,
                    evidence.EvidenceKind.Trim(),
                    StringComparison.Ordinal)
                && evidence.MinimumCount > 0)
            && policy.EvidenceRequirements
                .Select(evidence => evidence.EvidenceKind)
                .Distinct(StringComparer.Ordinal)
                .Count() == policy.EvidenceRequirements.Count
            && policy.AllowedStationModes.All(Enum.IsDefined)
            && policy.AllowedStationModes.Distinct().Count()
                == policy.AllowedStationModes.Count;
    }
}
