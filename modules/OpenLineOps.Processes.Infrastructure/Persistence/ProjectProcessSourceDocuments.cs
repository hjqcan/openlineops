namespace OpenLineOps.Processes.Infrastructure.Persistence;

internal sealed record ProjectProcessFlowDocument(
    int FormatVersion,
    string ResourceKind,
    string ProjectId,
    string ApplicationId,
    string ProcessDefinitionId,
    string VersionId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    string SourceRevision,
    ProjectProcessNodeDocument[] Nodes,
    ProjectProcessTransitionDocument[] Transitions)
{
    public const int CurrentFormatVersion = 1;
    public const string Kind = "OpenLineOps.ProcessFlow";
}

internal sealed record ProjectProcessNodeDocument(
    string NodeId,
    string Kind,
    string DisplayName,
    string? RequiredCapabilityId,
    string? CommandName,
    long? CommandTimeoutTicks,
    string? InputPayload,
    string? ScriptLanguage,
    string? ScriptEditorMode,
    string? ScriptVersion,
    long? ScriptTimeoutTicks,
    ProjectProcessScriptArtifactsDocument? ScriptArtifacts);

internal sealed record ProjectProcessScriptArtifactsDocument(
    ProjectProcessFileReference? BlocklyWorkspace,
    ProjectProcessFileReference? PythonSource);

internal sealed record ProjectProcessFileReference(
    string Path,
    string Sha256);

internal sealed record ProjectProcessTransitionDocument(
    string TransitionId,
    string FromNodeId,
    string ToNodeId,
    string? Label,
    string? LoopPolicy,
    int? MaxTraversals);
