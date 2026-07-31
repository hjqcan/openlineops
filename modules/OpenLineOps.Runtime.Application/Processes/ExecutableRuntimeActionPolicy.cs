namespace OpenLineOps.Runtime.Application.Processes;

public sealed record ExecutableRuntimeActionPolicy(
    RuntimeActionIdempotencyClass IdempotencyClass,
    RuntimeActionRecoveryPolicy RecoveryPolicy,
    RuntimeActionFailurePolicy FailurePolicy,
    IReadOnlyList<RuntimeActionResourceLock> ResourceLocks,
    IReadOnlyList<RuntimeActionEvidenceRequirement> EvidenceRequirements,
    IReadOnlyList<RuntimeActionStationMode> AllowedStationModes);

public sealed record RuntimeActionResourceLock(
    string ResourceId,
    RuntimeActionResourceLockMode Mode);

public sealed record RuntimeActionEvidenceRequirement(
    string EvidenceKind,
    int MinimumCount);

public enum RuntimeActionIdempotencyClass
{
    Idempotent = 0,
    Conditional = 1,
    NonIdempotent = 2
}

public enum RuntimeActionRecoveryPolicy
{
    AutomaticReplay = 0,
    ResumeFromCheckpoint = 1,
    ManualAuthorization = 2,
    NeverReplay = 3
}

public enum RuntimeActionFailurePolicy
{
    Continue = 0,
    Skip = 1,
    Retry = 2,
    Rework = 3,
    ManualDisposition = 4,
    Hold = 5,
    Terminate = 6
}

public enum RuntimeActionResourceLockMode
{
    Shared = 0,
    Exclusive = 1
}

public enum RuntimeActionStationMode
{
    Automatic = 0,
    Manual = 1,
    Setup = 2,
    Maintenance = 3,
    Simulation = 4
}
