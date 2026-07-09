using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

internal static class ProcessDefinitionSnapshotMapper
{
    public static PersistedProcessDefinition ToSnapshot(ProcessDefinition definition)
    {
        return new PersistedProcessDefinition(
            definition.Id.Value,
            definition.VersionId.Value,
            definition.DisplayName,
            definition.Status.ToString(),
            definition.CreatedAtUtc,
            definition.PublishedAtUtc,
            definition.Nodes
                .Select(ToSnapshot)
                .ToArray(),
            definition.Transitions
                .Select(ToSnapshot)
                .ToArray());
    }

    public static ProcessDefinition ToAggregate(PersistedProcessDefinition snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var definition = ProcessDefinition.Create(
            new ProcessDefinitionId(snapshot.DefinitionId),
            new ProcessVersionId(snapshot.VersionId),
            snapshot.DisplayName,
            snapshot.CreatedAtUtc);

        foreach (var node in snapshot.Nodes)
        {
            var result = definition.AddNode(ToAggregate(node));
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Persisted process definition {snapshot.DefinitionId} contains an invalid node: {result.Code}.");
            }
        }

        foreach (var transition in snapshot.Transitions)
        {
            var result = definition.AddTransition(ToAggregate(transition));
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Persisted process definition {snapshot.DefinitionId} contains an invalid transition: {result.Code}.");
            }
        }

        var status = ParseEnum<ProcessDefinitionStatus>(snapshot.Status, nameof(snapshot.Status));
        if (status == ProcessDefinitionStatus.Published)
        {
            if (snapshot.PublishedAtUtc is null)
            {
                throw new InvalidOperationException(
                    $"Persisted process definition {snapshot.DefinitionId} is published without a published timestamp.");
            }

            var publishResult = definition.Publish(snapshot.PublishedAtUtc.Value);
            if (!publishResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Persisted process definition {snapshot.DefinitionId} cannot be rehydrated as published: {publishResult.Code}.");
            }
        }
        else if (status != ProcessDefinitionStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Persisted process definition {snapshot.DefinitionId} has unsupported status {snapshot.Status}.");
        }

        definition.ClearDomainEvents();

        return definition;
    }

    private static PersistedProcessNode ToSnapshot(ProcessNode node)
    {
        return new PersistedProcessNode(
            node.Id.Value,
            node.Kind.ToString(),
            node.DisplayName,
            node.RequiredCapability?.Value,
            node.CommandName,
            node.CommandTimeout,
            node.InputPayload,
            node.ScriptLanguage,
            node.ScriptEditorMode?.ToString(),
            node.BlocklyWorkspaceJson,
            node.ScriptSourceCode,
            node.ScriptSourceHash,
            node.ScriptVersion,
            node.ScriptTimeout);
    }

    private static PersistedProcessTransition ToSnapshot(ProcessTransition transition)
    {
        return new PersistedProcessTransition(
            transition.Id.Value,
            transition.FromNodeId.Value,
            transition.ToNodeId.Value,
            transition.Label,
            transition.LoopPolicy.ToString(),
            transition.MaxTraversals);
    }

    private static ProcessNode ToAggregate(PersistedProcessNode node)
    {
        var nodeId = new ProcessNodeId(node.NodeId);
        var kind = ParseEnum<ProcessNodeKind>(node.Kind, nameof(node.Kind));

        return kind switch
        {
            ProcessNodeKind.Start => ProcessNode.Start(nodeId, node.DisplayName),
            ProcessNodeKind.Command => ProcessNode.Command(
                nodeId,
                node.DisplayName,
                string.IsNullOrWhiteSpace(node.RequiredCapabilityId)
                    ? null
                    : new ProcessCapabilityId(node.RequiredCapabilityId),
                node.CommandName,
                node.CommandTimeout,
                node.InputPayload),
            ProcessNodeKind.PythonScript => ProcessNode.PythonScript(
                nodeId,
                node.DisplayName,
                string.IsNullOrWhiteSpace(node.ScriptEditorMode)
                    ? null
                    : ParseEnum<ProcessScriptEditorMode>(node.ScriptEditorMode, nameof(node.ScriptEditorMode)),
                node.BlocklyWorkspaceJson,
                node.ScriptSourceCode,
                node.ScriptVersion,
                node.ScriptTimeout,
                node.InputPayload),
            ProcessNodeKind.Decision => ProcessNode.Decision(nodeId, node.DisplayName),
            ProcessNodeKind.Delay => ProcessNode.Delay(nodeId, node.DisplayName),
            ProcessNodeKind.End => ProcessNode.End(nodeId, node.DisplayName),
            _ => throw new InvalidOperationException($"Unsupported process node kind {node.Kind}.")
        };
    }

    private static ProcessTransition ToAggregate(PersistedProcessTransition transition)
    {
        return ProcessTransition.Create(
            new ProcessTransitionId(transition.TransitionId),
            new ProcessNodeId(transition.FromNodeId),
            new ProcessNodeId(transition.ToNodeId),
            transition.Label,
            ParseLoopPolicy(transition),
            transition.MaxTraversals);
    }

    private static ProcessTransitionLoopPolicy ParseLoopPolicy(PersistedProcessTransition transition)
    {
        return string.IsNullOrWhiteSpace(transition.LoopPolicy)
            ? ProcessTransitionLoopPolicy.None
            : ParseEnum<ProcessTransitionLoopPolicy>(transition.LoopPolicy, nameof(transition.LoopPolicy));
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Persisted {fieldName} value '{value}' is invalid.");
    }
}

internal sealed record PersistedProcessDefinition(
    string DefinitionId,
    string VersionId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    PersistedProcessNode[] Nodes,
    PersistedProcessTransition[] Transitions);

internal sealed record PersistedProcessNode(
    string NodeId,
    string Kind,
    string DisplayName,
    string? RequiredCapabilityId,
    string? CommandName,
    TimeSpan? CommandTimeout,
    string? InputPayload,
    string? ScriptLanguage,
    string? ScriptEditorMode,
    string? BlocklyWorkspaceJson,
    string? ScriptSourceCode,
    string? ScriptSourceHash,
    string? ScriptVersion,
    TimeSpan? ScriptTimeout);

internal sealed record PersistedProcessTransition(
    string TransitionId,
    string FromNodeId,
    string ToNodeId,
    string? Label,
    string? LoopPolicy,
    int? MaxTraversals);
