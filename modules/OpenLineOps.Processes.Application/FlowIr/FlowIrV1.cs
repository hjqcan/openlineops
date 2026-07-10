using System.Collections.Immutable;

namespace OpenLineOps.Processes.Application.FlowIr;

public static class FlowIrSchemaVersions
{
    public const string V1 = "openlineops.flow-ir/v1";
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
    FlowIrDynamicActionSlot? DynamicChildren,
    FlowIrSourceTrace Source);

public sealed record FlowIrExecutionPolicy(
    long TimeoutMilliseconds,
    int RetryLimit,
    FlowIrCancellationMode CancellationMode);

public sealed record FlowIrTargetReference(
    FlowIrTargetReferenceKind Kind,
    string Reference);

public sealed record FlowIrPythonScript(
    string Language,
    string SourceCode,
    string SourceHash,
    string Version);

public sealed record FlowIrDynamicActionSlot(
    string SlotId,
    FlowIrDynamicActionExpansionKind ExpansionKind,
    string ChildActionIdPrefix,
    int SequenceBase,
    bool IsCompileTimeResolved,
    FlowIrChildSourceMappingMode SourceMappingMode,
    FlowIrSourceTrace Source);

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
    AutomationModule = 1,
    EquipmentNode = 2,
    SlotGroup = 3,
    Slot = 4,
    Dut = 5,
    System = 6,
    Capability = 7,
    Driver = 8
}

public enum FlowIrCancellationMode
{
    Cooperative = 1
}

public enum FlowIrDynamicActionExpansionKind
{
    RuntimeAutomationPlan = 1
}

public enum FlowIrChildSourceMappingMode
{
    ContainerOnly = 1,
    ExactBlock = 2
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
