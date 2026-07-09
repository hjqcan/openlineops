namespace OpenLineOps.Processes.Api.Models;

public sealed record CreateProcessNodeRequest(
    string? NodeId,
    string? Kind,
    string? DisplayName,
    string? RequiredCapability,
    string? CommandName,
    int? TimeoutSeconds,
    string? InputPayload,
    string? ScriptEditorMode,
    string? BlocklyWorkspaceJson,
    string? ScriptSourceCode,
    string? ScriptVersion);
