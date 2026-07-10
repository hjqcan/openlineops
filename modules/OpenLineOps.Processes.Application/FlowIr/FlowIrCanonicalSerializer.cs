using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Application.Scripting;

namespace OpenLineOps.Processes.Application.FlowIr;

public sealed class FlowIrCanonicalSerializer : IFlowIrCanonicalSerializer
{
    private static readonly JsonSerializerOptions ReadOptions = CreateReadOptions();

    public Result<FlowIrCanonicalArtifact> Serialize(FlowIrDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var validationError = Validate(document);
        if (validationError is not null)
        {
            return Result.Failure<FlowIrCanonicalArtifact>(validationError);
        }

        var normalized = Normalize(document);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = false,
                   SkipValidation = false
               }))
        {
            WriteDocument(writer, normalized);
        }

        var bytes = buffer.WrittenSpan;
        var canonicalJson = Encoding.UTF8.GetString(bytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return Result.Success(new FlowIrCanonicalArtifact(
            normalized.SchemaVersion,
            canonicalJson,
            sha256));
    }

    public Result<FlowIrDocument> Deserialize(string canonicalJson)
    {
        if (string.IsNullOrWhiteSpace(canonicalJson))
        {
            return Result.Failure<FlowIrDocument>(Invalid("Canonical JSON is required."));
        }

        FlowIrDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<FlowIrDocument>(canonicalJson, ReadOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Result.Failure<FlowIrDocument>(Invalid(
                $"Canonical JSON is invalid: {exception.Message}"));
        }

        if (document is null)
        {
            return Result.Failure<FlowIrDocument>(Invalid("Canonical JSON is empty."));
        }

        var serializationResult = Serialize(document);
        if (serializationResult.IsFailure)
        {
            return Result.Failure<FlowIrDocument>(serializationResult.Error);
        }

        if (!string.Equals(
                canonicalJson,
                serializationResult.Value.CanonicalJson,
                StringComparison.Ordinal))
        {
            return Result.Failure<FlowIrDocument>(Invalid(
                "Flow IR JSON is valid but is not in canonical form."));
        }

        return Result.Success(Normalize(document));
    }

    private static FlowIrDocument Normalize(FlowIrDocument document)
    {
        return document with
        {
            Nodes = document.Nodes
                .OrderBy(node => node.NodeId, StringComparer.Ordinal)
                .Select(node => node with
                {
                    Actions = node.Actions
                        .OrderBy(action => action.ActionId, StringComparer.Ordinal)
                        .ToImmutableArray()
                })
                .ToImmutableArray(),
            Transitions = document.Transitions
                .OrderBy(transition => transition.TransitionId, StringComparer.Ordinal)
                .ToImmutableArray()
        };
    }

    private static ApplicationError? Validate(FlowIrDocument document)
    {
        if (!string.Equals(document.SchemaVersion, FlowIrSchemaVersions.V1, StringComparison.Ordinal))
        {
            return Invalid($"Schema version '{document.SchemaVersion}' is not supported.");
        }

        if (!IsCanonicalValue(document.ProcessDefinitionId)
            || !IsCanonicalValue(document.ProcessVersionId)
            || !IsCanonicalValue(document.DisplayName)
            || !IsCanonicalValue(document.StartNodeId))
        {
            return Invalid("Process identity, display name, and start node id must be non-empty canonical strings.");
        }

        if (document.Nodes.IsDefaultOrEmpty)
        {
            return Invalid("Nodes collection is required and cannot be empty.");
        }

        if (document.Transitions.IsDefault)
        {
            return Invalid("Transitions collection is required.");
        }

        var nodesById = new Dictionary<string, FlowIrNode>(StringComparer.Ordinal);
        foreach (var node in document.Nodes)
        {
            if (node is null)
            {
                return Invalid("Nodes collection cannot contain null entries.");
            }

            if (!IsCanonicalValue(node.NodeId)
                || !IsCanonicalValue(node.DisplayName)
                || !Enum.IsDefined(node.Kind))
            {
                return Invalid("Every node must have a non-empty canonical id and display name.");
            }

            if (!nodesById.TryAdd(node.NodeId, node))
            {
                return Invalid($"Node id '{node.NodeId}' is duplicated.");
            }

            if (node.Actions.IsDefault)
            {
                return Invalid($"Node {node.NodeId} has no actions collection.");
            }

            var sourceError = ValidateSource(document, node.Source, FlowIrSourceElementKind.ProcessNode, node.NodeId);
            if (sourceError is not null)
            {
                return sourceError;
            }

            var nodeError = ValidateNode(document, node);
            if (nodeError is not null)
            {
                return nodeError;
            }
        }

        if (!nodesById.TryGetValue(document.StartNodeId, out var startNode)
            || startNode.Kind != FlowIrNodeKind.Start
            || document.Nodes.Count(node => node.Kind == FlowIrNodeKind.Start) != 1)
        {
            return Invalid($"Start node '{document.StartNodeId}' is missing or is not a Start node.");
        }

        if (!document.Nodes.Any(node => node.Kind is FlowIrNodeKind.Command or FlowIrNodeKind.PythonScript))
        {
            return Invalid("Flow IR must contain at least one executable action node.");
        }

        var transitionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transition in document.Transitions)
        {
            if (transition is null)
            {
                return Invalid("Transitions collection cannot contain null entries.");
            }

            if (!IsCanonicalValue(transition.TransitionId)
                || !IsCanonicalValue(transition.FromNodeId)
                || !IsCanonicalValue(transition.ToNodeId))
            {
                return Invalid("Every transition must have canonical identity and endpoint values.");
            }

            if (!transitionIds.Add(transition.TransitionId))
            {
                return Invalid($"Transition id '{transition.TransitionId}' is duplicated.");
            }

            if (!nodesById.ContainsKey(transition.FromNodeId)
                || !nodesById.ContainsKey(transition.ToNodeId))
            {
                return Invalid($"Transition {transition.TransitionId} references a missing node.");
            }

            if (transition.Label is not null && !IsCanonicalValue(transition.Label))
            {
                return Invalid($"Transition {transition.TransitionId} label is not canonical.");
            }

            if ((transition.LoopPolicy == FlowIrLoopPolicy.None && transition.MaxTraversals is not null)
                || (transition.LoopPolicy == FlowIrLoopPolicy.Counted && transition.MaxTraversals is not > 0)
                || !Enum.IsDefined(transition.LoopPolicy))
            {
                return Invalid($"Transition {transition.TransitionId} loop metadata is invalid.");
            }

            var sourceError = ValidateSource(
                document,
                transition.Source,
                FlowIrSourceElementKind.ProcessTransition,
                transition.TransitionId);
            if (sourceError is not null)
            {
                return sourceError;
            }

            if (transition.Source.ContentHash is not null)
            {
                return Invalid($"Transition {transition.TransitionId} cannot carry a content hash.");
            }
        }

        foreach (var transitionGroup in document.Transitions.GroupBy(
                     transition => transition.FromNodeId,
                     StringComparer.Ordinal))
        {
            var outgoing = transitionGroup.ToArray();
            if (outgoing.Length <= 1)
            {
                continue;
            }

            if (nodesById[transitionGroup.Key].Kind != FlowIrNodeKind.Decision)
            {
                return Invalid($"Node {transitionGroup.Key} branches outside a Decision node.");
            }

            var duplicateLabel = outgoing
                .Select(transition => transition.Label ?? "default")
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1)
                ?.Key;
            if (duplicateLabel is not null)
            {
                return Invalid($"Decision node {transitionGroup.Key} has duplicate label '{duplicateLabel}'.");
            }
        }

        return null;
    }

    private static ApplicationError? ValidateNode(FlowIrDocument document, FlowIrNode node)
    {
        var isExecutable = node.Kind is FlowIrNodeKind.Command or FlowIrNodeKind.PythonScript;
        if (!isExecutable)
        {
            return node.Actions.Length == 0 && node.Source.ContentHash is null
                ? null
                : Invalid($"Routing node {node.NodeId} cannot contain actions or a content hash.");
        }

        if (node.Actions.Length != 1)
        {
            return Invalid($"Executable node {node.NodeId} must contain exactly one action in Flow IR v1.");
        }

        var action = node.Actions[0];
        if (action is null)
        {
            return Invalid($"Executable node {node.NodeId} cannot contain a null action.");
        }

        if (action.Target is null
            || action.Execution is null
            || action.Source is null
            || !string.Equals(action.ActionId, $"{node.NodeId}:action:1", StringComparison.Ordinal)
            || !string.Equals(action.DisplayName, node.DisplayName, StringComparison.Ordinal)
            || !IsCanonicalValue(action.RequiredCapability)
            || !IsCanonicalValue(action.CommandName)
            || action.Target.Kind != FlowIrTargetReferenceKind.Capability
            || !string.Equals(action.Target.Reference, action.RequiredCapability, StringComparison.Ordinal))
        {
            return Invalid($"Action on node {node.NodeId} has invalid identity, command, or target metadata.");
        }

        if (action.Execution.TimeoutMilliseconds <= 0
            || action.Execution.TimeoutMilliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond
            || action.Execution.RetryLimit != 0
            || action.Execution.CancellationMode != FlowIrCancellationMode.Cooperative)
        {
            return Invalid($"Action {action.ActionId} execution policy is not supported by Flow IR v1.");
        }

        var sourceError = ValidateSource(
            document,
            action.Source,
            FlowIrSourceElementKind.ProcessNode,
            node.NodeId);
        if (sourceError is not null || action.Source != node.Source)
        {
            return sourceError ?? Invalid($"Action {action.ActionId} source trace differs from its node source trace.");
        }

        return node.Kind switch
        {
            FlowIrNodeKind.Command when action.Kind == FlowIrActionKind.DeviceCommand
                                         && action.PythonScript is null
                                         && action.DynamicChildren is null
                                         && action.Source.ContentHash is null => null,
            FlowIrNodeKind.PythonScript when action.Kind == FlowIrActionKind.PythonScript
                                             && string.Equals(
                                                 action.RequiredCapability,
                                                 RuntimeScriptCommand.PythonCapability,
                                                 StringComparison.Ordinal)
                                             && string.Equals(
                                                 action.CommandName,
                                                 RuntimeScriptCommand.PythonCommandName,
                                                 StringComparison.Ordinal) =>
                ValidatePythonAction(node, action),
            _ => Invalid($"Action {action.ActionId} kind does not match node kind {node.Kind}.")
        };
    }

    private static ApplicationError? ValidatePythonAction(FlowIrNode node, FlowIrAction action)
    {
        var script = action.PythonScript;
        var slot = action.DynamicChildren;
        if (script is null
            || !string.Equals(script.Language, "Python", StringComparison.Ordinal)
            || (script.EditorMode is not "Blockly" and not "ManualCode")
            || string.IsNullOrWhiteSpace(script.SourceCode)
            || !IsCanonicalValue(script.SourceHash)
            || !IsSha256(script.SourceHash)
            || !IsCanonicalValue(script.Version)
            || !string.Equals(action.Source.ContentHash, script.SourceHash, StringComparison.Ordinal))
        {
            return Invalid($"Python action {action.ActionId} has invalid source metadata.");
        }

        if (string.Equals(script.EditorMode, "Blockly", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(script.BlocklyWorkspaceJson))
        {
            return Invalid($"Python action {action.ActionId} Blockly workspace JSON is required.");
        }

        var computedSourceHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(script.SourceCode)))
            .ToLowerInvariant();
        if (!string.Equals(script.SourceHash, computedSourceHash, StringComparison.Ordinal))
        {
            return Invalid($"Python action {action.ActionId} source hash does not match its UTF-8 source code.");
        }

        if (script.BlocklyWorkspaceJson is not null)
        {
            try
            {
                using var _ = JsonDocument.Parse(script.BlocklyWorkspaceJson);
            }
            catch (JsonException exception)
            {
                return Invalid(
                    $"Python action {action.ActionId} Blockly workspace JSON is invalid: {exception.Message}");
            }
        }

        if (slot is null
            || !string.Equals(slot.SlotId, $"{action.ActionId}:automation-plan", StringComparison.Ordinal)
            || slot.ExpansionKind != FlowIrDynamicActionExpansionKind.RuntimeAutomationPlan
            || !string.Equals(slot.ChildActionIdPrefix, $"{action.ActionId}:child:", StringComparison.Ordinal)
            || slot.SequenceBase != 1
            || slot.IsCompileTimeResolved
            || slot.SourceMappingMode != FlowIrChildSourceMappingMode.ContainerOnly
            || slot.Source is null
            || slot.Source != node.Source)
        {
            return Invalid($"Python action {action.ActionId} dynamic child slot is invalid.");
        }

        return null;
    }

    private static ApplicationError? ValidateSource(
        FlowIrDocument document,
        FlowIrSourceTrace source,
        FlowIrSourceElementKind elementKind,
        string elementId)
    {
        if (source is null)
        {
            return Invalid($"Source trace for {elementKind} {elementId} is required.");
        }

        return string.Equals(source.ProcessDefinitionId, document.ProcessDefinitionId, StringComparison.Ordinal)
               && string.Equals(source.ProcessVersionId, document.ProcessVersionId, StringComparison.Ordinal)
               && source.ElementKind == elementKind
               && string.Equals(source.ElementId, elementId, StringComparison.Ordinal)
            ? null
            : Invalid($"Source trace for {elementKind} {elementId} does not match the Flow IR identity.");
    }

    private static bool IsCanonicalValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && string.Equals(value, value.Trim(), StringComparison.Ordinal);
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64
               && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
               && value.All(Uri.IsHexDigit);
    }

    private static ApplicationError Invalid(string message)
    {
        return ApplicationError.Validation("Processes.FlowIrDocumentInvalid", message);
    }

    private static JsonSerializerOptions CreateReadOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static void WriteDocument(Utf8JsonWriter writer, FlowIrDocument document)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", document.SchemaVersion);
        writer.WriteString("processDefinitionId", document.ProcessDefinitionId);
        writer.WriteString("processVersionId", document.ProcessVersionId);
        writer.WriteString("displayName", document.DisplayName);
        writer.WriteString("startNodeId", document.StartNodeId);
        writer.WritePropertyName("nodes");
        writer.WriteStartArray();
        foreach (var node in document.Nodes)
        {
            WriteNode(writer, node);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("transitions");
        writer.WriteStartArray();
        foreach (var transition in document.Transitions)
        {
            WriteTransition(writer, transition);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, FlowIrNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("nodeId", node.NodeId);
        writer.WriteString("kind", NodeKind(node.Kind));
        writer.WriteString("displayName", node.DisplayName);
        writer.WritePropertyName("actions");
        writer.WriteStartArray();
        foreach (var action in node.Actions)
        {
            WriteAction(writer, action);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("source");
        WriteSource(writer, node.Source);
        writer.WriteEndObject();
    }

    private static void WriteAction(Utf8JsonWriter writer, FlowIrAction action)
    {
        writer.WriteStartObject();
        writer.WriteString("actionId", action.ActionId);
        writer.WriteString("kind", ActionKind(action.Kind));
        writer.WriteString("displayName", action.DisplayName);
        writer.WriteString("requiredCapability", action.RequiredCapability);
        writer.WriteString("commandName", action.CommandName);
        writer.WritePropertyName("target");
        writer.WriteStartObject();
        writer.WriteString("kind", TargetKind(action.Target.Kind));
        writer.WriteString("reference", action.Target.Reference);
        writer.WriteEndObject();
        WriteNullableString(writer, "inputPayload", action.InputPayload);
        writer.WritePropertyName("execution");
        writer.WriteStartObject();
        writer.WriteNumber("timeoutMilliseconds", action.Execution.TimeoutMilliseconds);
        writer.WriteNumber("retryLimit", action.Execution.RetryLimit);
        writer.WriteString("cancellationMode", CancellationMode(action.Execution.CancellationMode));
        writer.WriteEndObject();
        writer.WritePropertyName("pythonScript");
        if (action.PythonScript is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WritePythonScript(writer, action.PythonScript);
        }

        writer.WritePropertyName("dynamicChildren");
        if (action.DynamicChildren is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteDynamicChildren(writer, action.DynamicChildren);
        }

        writer.WritePropertyName("source");
        WriteSource(writer, action.Source);
        writer.WriteEndObject();
    }

    private static void WritePythonScript(Utf8JsonWriter writer, FlowIrPythonScript script)
    {
        writer.WriteStartObject();
        writer.WriteString("language", script.Language);
        writer.WriteString("editorMode", script.EditorMode);
        writer.WriteString("sourceCode", script.SourceCode);
        writer.WriteString("sourceHash", script.SourceHash);
        writer.WriteString("version", script.Version);
        WriteNullableString(writer, "blocklyWorkspaceJson", script.BlocklyWorkspaceJson);
        writer.WriteEndObject();
    }

    private static void WriteDynamicChildren(Utf8JsonWriter writer, FlowIrDynamicActionSlot slot)
    {
        writer.WriteStartObject();
        writer.WriteString("slotId", slot.SlotId);
        writer.WriteString("expansionKind", ExpansionKind(slot.ExpansionKind));
        writer.WriteString("childActionIdPrefix", slot.ChildActionIdPrefix);
        writer.WriteNumber("sequenceBase", slot.SequenceBase);
        writer.WriteBoolean("isCompileTimeResolved", slot.IsCompileTimeResolved);
        writer.WriteString("sourceMappingMode", SourceMappingMode(slot.SourceMappingMode));
        writer.WritePropertyName("source");
        WriteSource(writer, slot.Source);
        writer.WriteEndObject();
    }

    private static void WriteTransition(Utf8JsonWriter writer, FlowIrTransition transition)
    {
        writer.WriteStartObject();
        writer.WriteString("transitionId", transition.TransitionId);
        writer.WriteString("fromNodeId", transition.FromNodeId);
        writer.WriteString("toNodeId", transition.ToNodeId);
        WriteNullableString(writer, "label", transition.Label);
        writer.WriteString("loopPolicy", LoopPolicy(transition.LoopPolicy));
        if (transition.MaxTraversals is null)
        {
            writer.WriteNull("maxTraversals");
        }
        else
        {
            writer.WriteNumber("maxTraversals", transition.MaxTraversals.Value);
        }

        writer.WritePropertyName("source");
        WriteSource(writer, transition.Source);
        writer.WriteEndObject();
    }

    private static void WriteSource(Utf8JsonWriter writer, FlowIrSourceTrace source)
    {
        writer.WriteStartObject();
        writer.WriteString("processDefinitionId", source.ProcessDefinitionId);
        writer.WriteString("processVersionId", source.ProcessVersionId);
        writer.WriteString("elementKind", SourceElementKind(source.ElementKind));
        writer.WriteString("elementId", source.ElementId);
        WriteNullableString(writer, "contentHash", source.ContentHash);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static string NodeKind(FlowIrNodeKind value) => value switch
    {
        FlowIrNodeKind.Start => "start",
        FlowIrNodeKind.Command => "command",
        FlowIrNodeKind.Decision => "decision",
        FlowIrNodeKind.Delay => "delay",
        FlowIrNodeKind.End => "end",
        FlowIrNodeKind.PythonScript => "pythonScript",
        _ => throw new InvalidOperationException($"Unsupported Flow IR node kind {value}.")
    };

    private static string ActionKind(FlowIrActionKind value) => value switch
    {
        FlowIrActionKind.DeviceCommand => "deviceCommand",
        FlowIrActionKind.PythonScript => "pythonScript",
        _ => throw new InvalidOperationException($"Unsupported Flow IR action kind {value}.")
    };

    private static string TargetKind(FlowIrTargetReferenceKind value) => value switch
    {
        FlowIrTargetReferenceKind.Capability => "capability",
        _ => throw new InvalidOperationException($"Unsupported Flow IR target kind {value}.")
    };

    private static string CancellationMode(FlowIrCancellationMode value) => value switch
    {
        FlowIrCancellationMode.Cooperative => "cooperative",
        _ => throw new InvalidOperationException($"Unsupported Flow IR cancellation mode {value}.")
    };

    private static string ExpansionKind(FlowIrDynamicActionExpansionKind value) => value switch
    {
        FlowIrDynamicActionExpansionKind.RuntimeAutomationPlan => "runtimeAutomationPlan",
        _ => throw new InvalidOperationException($"Unsupported Flow IR expansion kind {value}.")
    };

    private static string SourceMappingMode(FlowIrChildSourceMappingMode value) => value switch
    {
        FlowIrChildSourceMappingMode.ContainerOnly => "containerOnly",
        _ => throw new InvalidOperationException($"Unsupported Flow IR source mapping mode {value}.")
    };

    private static string LoopPolicy(FlowIrLoopPolicy value) => value switch
    {
        FlowIrLoopPolicy.None => "none",
        FlowIrLoopPolicy.Counted => "counted",
        _ => throw new InvalidOperationException($"Unsupported Flow IR loop policy {value}.")
    };

    private static string SourceElementKind(FlowIrSourceElementKind value) => value switch
    {
        FlowIrSourceElementKind.ProcessNode => "processNode",
        FlowIrSourceElementKind.ProcessTransition => "processTransition",
        _ => throw new InvalidOperationException($"Unsupported Flow IR source element kind {value}.")
    };
}
