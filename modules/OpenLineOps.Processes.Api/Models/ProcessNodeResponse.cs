namespace OpenLineOps.Processes.Api.Models;

public sealed record ProcessNodeResponse(
    string NodeId,
    string Kind,
    string DisplayName,
    string? RequiredCapability,
    string? CommandName,
    string? TargetKind,
    string? TargetId,
    int? TimeoutSeconds,
    string? InputPayload,
    string? ScriptLanguage,
    string? BlocklyWorkspaceJson,
    string? ScriptSourceCode,
    string? ScriptSourceHash,
    string? ScriptVersion);
