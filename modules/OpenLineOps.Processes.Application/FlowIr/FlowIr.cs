using System.Collections.Immutable;

namespace OpenLineOps.Processes.Application.FlowIr;

public static class FlowIrSchema
{
    public const string Current = "openlineops.flow-ir";
}

public sealed record FlowIrCompilation(
    FlowIrDocument Document,
    ImmutableArray<FlowIrCompilationDiagnostic> Diagnostics);

public sealed record FlowIrCanonicalArtifact(
    string SchemaVersion,
    string CanonicalJson,
    string Sha256);

public sealed record FlowIrDocument(
    string SchemaVersion,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string DisplayName,
    string StartNodeId,
    ImmutableArray<FlowIrNode> Nodes,
    ImmutableArray<FlowIrTransition> Transitions,
    ImmutableArray<FlowIrBlockDependency> BlockDependencies);

public sealed record FlowIrBlockDependency(
    string BlockType,
    int Version,
    string ContractSchemaVersion,
    string ContractSha256)
{
    public string LockId => $"{BlockType}@{Version}#{ContractSha256}";
}

public sealed record FlowIrNode(
    string NodeId,
    FlowIrNodeKind Kind,
    string DisplayName,
    ImmutableArray<FlowIrAction> Actions,
    FlowIrSourceTrace Source);

public sealed record FlowIrAction(
    string ActionId,
    FlowIrActionKind Kind,
    string DisplayName,
    string RequiredCapability,
    string CommandName,
    FlowIrTargetReference Target,
    string? InputPayload,
    FlowIrExecutionPolicy Execution,
    FlowIrPythonScript? PythonScript,
    FlowIrSourceTrace Source,
    FlowIrOperationalPolicy? OperationalPolicy = null);

public sealed record FlowIrExecutionPolicy(
    long TimeoutMilliseconds,
    int RetryLimit,
    FlowIrCancellationMode CancellationMode);

public sealed record FlowIrOperationalPolicy(
    FlowIrIdempotencyClass IdempotencyClass,
    FlowIrRecoveryPolicy RecoveryPolicy,
    FlowIrFailurePolicy FailurePolicy,
    ImmutableArray<FlowIrResourceLock> ResourceLocks,
    ImmutableArray<FlowIrEvidenceRequirement> EvidenceRequirements,
    ImmutableArray<FlowIrStationMode> AllowedStationModes);

public sealed record FlowIrResourceLock(
    string ResourceId,
    FlowIrResourceLockMode Mode);

public sealed record FlowIrEvidenceRequirement(
    string EvidenceKind,
    int MinimumCount);

public sealed record FlowIrTargetReference(
    FlowIrTargetReferenceKind Kind,
    string Reference);

public sealed record FlowIrPythonScript(
    string Language,
    string SourceCode,
    string SourceHash,
    string Version);

public sealed record FlowIrTransition(
    string TransitionId,
    string FromNodeId,
    string ToNodeId,
    string? Label,
    FlowIrLoopPolicy LoopPolicy,
    int? MaxTraversals,
    FlowIrSourceTrace Source);

public sealed record FlowIrSourceTrace(
    string ProcessDefinitionId,
    string ProcessVersionId,
    FlowIrSourceElementKind ElementKind,
    string ElementId,
    string? ContentHash);

public sealed record FlowIrCompilationDiagnostic(
    FlowIrDiagnosticSeverity Severity,
    string Code,
    string Message,
    FlowIrSourceTrace Source);

public enum FlowIrNodeKind
{
    Start = 0,
    Command = 1,
    Decision = 2,
    Delay = 3,
    End = 4,
    PythonScript = 5,
    Blockly = 6
}

public enum FlowIrActionKind
{
    DeviceCommand = 1,
    PythonScript = 2
}

public enum FlowIrTargetReferenceKind
{
    System = 1,
    SlotGroup = 2,
    Slot = 3,
    ProductionUnit = 4,
    Capability = 5,
    Driver = 6
}

public enum FlowIrCancellationMode
{
    Cooperative = 1
}

public enum FlowIrIdempotencyClass
{
    Idempotent = 0,
    Conditional = 1,
    NonIdempotent = 2
}

public enum FlowIrRecoveryPolicy
{
    AutomaticReplay = 0,
    ResumeFromCheckpoint = 1,
    ManualAuthorization = 2,
    NeverReplay = 3
}

public enum FlowIrFailurePolicy
{
    Continue = 0,
    Skip = 1,
    Retry = 2,
    Rework = 3,
    ManualDisposition = 4,
    Hold = 5,
    Terminate = 6
}

public enum FlowIrResourceLockMode
{
    Shared = 0,
    Exclusive = 1
}

public enum FlowIrStationMode
{
    Automatic = 0,
    Manual = 1,
    Setup = 2,
    Maintenance = 3,
    Simulation = 4
}

public enum FlowIrLoopPolicy
{
    None = 0,
    Counted = 1
}

public enum FlowIrSourceElementKind
{
    ProcessNode = 1,
    ProcessTransition = 2,
    BlocklyBlock = 3
}

public enum FlowIrDiagnosticSeverity
{
    Warning = 1
}
