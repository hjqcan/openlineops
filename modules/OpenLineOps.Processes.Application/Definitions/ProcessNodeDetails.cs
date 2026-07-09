namespace OpenLineOps.Processes.Application.Definitions;

public sealed record ProcessNodeDetails(
    string NodeId,
    string Kind,
    string DisplayName,
    string? RequiredCapability,
    string? CommandName,
    int? TimeoutSeconds,
    string? InputPayload,
    string? ScriptLanguage,
    string? ScriptEditorMode,
    string? BlocklyWorkspaceJson,
    string? ScriptSourceCode,
    string? ScriptSourceHash,
    string? ScriptVersion);
