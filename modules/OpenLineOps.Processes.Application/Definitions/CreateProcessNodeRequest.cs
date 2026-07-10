namespace OpenLineOps.Processes.Application.Definitions;

public sealed record CreateProcessNodeRequest(
    string NodeId,
    string Kind,
    string DisplayName,
    string? RequiredCapability,
    string? CommandName,
    string? TargetKind,
    string? TargetId,
    int? TimeoutSeconds,
    string? InputPayload,
    string? BlocklyWorkspaceJson,
    string? ScriptSourceCode,
    string? ScriptVersion);
