using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Processes.Domain.Definitions;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

internal static class ProjectProcessSourceMapper
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async ValueTask<ProjectProcessFlowDocument> FromAggregateAsync(
        ProjectApplicationWorkspaceScope scope,
        ProcessDefinition definition,
        CancellationToken cancellationToken)
    {
        var snapshot = ProcessDefinitionSnapshotMapper.ToSnapshot(definition);
        var flowDirectory = ProjectProcessResourcePath.GetFlowDirectory(scope, definition.Id.Value);
        var nodes = new List<ProjectProcessNodeDocument>(snapshot.Nodes.Length);

        foreach (var node in snapshot.Nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectProcessScriptArtifactsDocument? artifacts = null;

            if (string.Equals(node.Kind, "PythonScript", StringComparison.Ordinal)
                || string.Equals(node.Kind, "Blockly", StringComparison.Ordinal))
            {
                ProjectProcessFileReference? workspaceReference = null;
                ProjectProcessFileReference? sourceReference = null;

                if (string.Equals(node.Kind, "Blockly", StringComparison.Ordinal)
                    && node.BlocklyWorkspaceJson is not null)
                {
                    var workspaceBytes = StrictUtf8.GetBytes(node.BlocklyWorkspaceJson);
                    var workspaceSha256 = ProjectProcessResourceFileStore.ComputeSha256(workspaceBytes);
                    var workspacePath = ProjectProcessResourcePath.GetWorkspaceArtifactPath(
                        scope,
                        definition.Id.Value,
                        node.NodeId,
                        workspaceSha256);
                    workspaceReference = await ProjectProcessResourceFileStore
                        .SaveArtifactAsync(flowDirectory, workspacePath, workspaceBytes, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (string.Equals(node.Kind, "PythonScript", StringComparison.Ordinal)
                    && node.ScriptSourceCode is not null)
                {
                    var sourceBytes = StrictUtf8.GetBytes(node.ScriptSourceCode);
                    var sourceSha256 = ProjectProcessResourceFileStore.ComputeSha256(sourceBytes);
                    if (!string.Equals(sourceSha256, node.ScriptSourceHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Python source hash for node {node.NodeId} does not match its content.");
                    }

                    var sourcePath = ProjectProcessResourcePath.GetPythonArtifactPath(
                        scope,
                        definition.Id.Value,
                        node.NodeId,
                        sourceSha256);
                    sourceReference = await ProjectProcessResourceFileStore
                        .SaveArtifactAsync(flowDirectory, sourcePath, sourceBytes, cancellationToken)
                        .ConfigureAwait(false);
                }

                artifacts = new ProjectProcessScriptArtifactsDocument(workspaceReference, sourceReference);
            }

            nodes.Add(new ProjectProcessNodeDocument(
                node.NodeId,
                node.Kind,
                node.DisplayName,
                node.RequiredCapabilityId,
                node.CommandName,
                node.CommandTimeout?.Ticks,
                node.InputPayload,
                node.ScriptLanguage,
                node.ScriptVersion,
                node.ScriptTimeout?.Ticks,
                artifacts));
        }

        var document = new ProjectProcessFlowDocument(
            ProjectProcessFlowDocument.CurrentFormatVersion,
            ProjectProcessFlowDocument.Kind,
            scope.ApplicationId,
            snapshot.DefinitionId,
            snapshot.VersionId,
            snapshot.DisplayName,
            snapshot.Status,
            snapshot.CreatedAtUtc,
            snapshot.PublishedAtUtc,
            string.Empty,
            nodes.ToArray(),
            snapshot.Transitions
                .OrderBy(transition => transition.TransitionId, StringComparer.Ordinal)
                .Select(transition => new ProjectProcessTransitionDocument(
                    transition.TransitionId,
                    transition.FromNodeId,
                    transition.ToNodeId,
                    transition.Label,
                    transition.LoopPolicy,
                    transition.MaxTraversals))
                .ToArray());

        return document with { SourceRevision = ComputeSourceRevision(document) };
    }

    public static async ValueTask<ProcessDefinition> ToAggregateAsync(
        ProjectApplicationWorkspaceScope scope,
        string flowDirectory,
        ProjectProcessFlowDocument document,
        CancellationToken cancellationToken)
    {
        ValidateDocument(scope, document);

        var nodes = new List<PersistedProcessNode>((document.Nodes ?? []).Length);
        foreach (var node in document.Nodes ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? workspaceJson = null;
            string? sourceCode = null;
            string? sourceHash = null;
            if (node.ScriptArtifacts?.BlocklyWorkspace is not null)
            {
                var reference = node.ScriptArtifacts.BlocklyWorkspace;
                var path = ProjectProcessResourcePath.ResolveRelativeFile(scope, flowDirectory, reference.Path);
                var bytes = await ProjectProcessResourceFileStore
                    .LoadVerifiedArtifactAsync(path, reference.Sha256, cancellationToken)
                    .ConfigureAwait(false);
                workspaceJson = DecodeUtf8(path, bytes);
            }

            if (node.ScriptArtifacts?.PythonSource is not null)
            {
                var reference = node.ScriptArtifacts.PythonSource;
                var path = ProjectProcessResourcePath.ResolveRelativeFile(scope, flowDirectory, reference.Path);
                var bytes = await ProjectProcessResourceFileStore
                    .LoadVerifiedArtifactAsync(path, reference.Sha256, cancellationToken)
                    .ConfigureAwait(false);
                sourceCode = DecodeUtf8(path, bytes);
                sourceHash = reference.Sha256;
            }

            nodes.Add(new PersistedProcessNode(
                node.NodeId,
                node.Kind,
                node.DisplayName,
                node.RequiredCapabilityId,
                node.CommandName,
                node.CommandTimeoutTicks is null ? null : TimeSpan.FromTicks(node.CommandTimeoutTicks.Value),
                node.InputPayload,
                node.ScriptLanguage,
                workspaceJson,
                sourceCode,
                sourceHash,
                node.ScriptVersion,
                node.ScriptTimeoutTicks is null ? null : TimeSpan.FromTicks(node.ScriptTimeoutTicks.Value)));
        }

        var snapshot = new PersistedProcessDefinition(
            document.ProcessDefinitionId,
            document.VersionId,
            document.DisplayName,
            document.Status,
            document.CreatedAtUtc,
            document.PublishedAtUtc,
            nodes.ToArray(),
            (document.Transitions ?? []).Select(transition => new PersistedProcessTransition(
                transition.TransitionId,
                transition.FromNodeId,
                transition.ToNodeId,
                transition.Label,
                transition.LoopPolicy,
                transition.MaxTraversals)).ToArray());

        var definition = ProcessDefinitionSnapshotMapper.ToAggregate(snapshot);
        foreach (var node in definition.Nodes.Where(node => node.ScriptSourceCode is not null))
        {
            var expectedHash = nodes.Single(candidate => candidate.NodeId == node.Id.Value).ScriptSourceHash;
            if (!string.Equals(node.ScriptSourceHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Python source hash for node {node.Id} changed while restoring the process definition.");
            }
        }

        return definition;
    }

    private static void ValidateDocument(
        ProjectApplicationWorkspaceScope scope,
        ProjectProcessFlowDocument document)
    {
        if (document.FormatVersion != ProjectProcessFlowDocument.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Project process format version {document.FormatVersion} is not supported.");
        }

        if (!string.Equals(document.ResourceKind, ProjectProcessFlowDocument.Kind, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Project process resource kind '{document.ResourceKind}' is not supported.");
        }

        if (!string.Equals(document.ApplicationId, scope.ApplicationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Project process belongs to application {document.ApplicationId}, not {scope.ApplicationId}.");
        }

        var expectedRevision = ComputeSourceRevision(document with { SourceRevision = string.Empty });
        if (!string.Equals(document.SourceRevision, expectedRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Project process {document.ProcessDefinitionId} source revision does not match its flow document.");
        }
    }

    private static string ComputeSourceRevision(ProjectProcessFlowDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            document with { SourceRevision = string.Empty },
            CanonicalJsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string DecodeUtf8(string path, byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Project process artifact '{path}' is not valid UTF-8.", exception);
        }
    }
}
