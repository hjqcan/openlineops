using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Scripting;
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
                .Select(node => node with
                {
                    Actions = node.Actions
                        .Select(NormalizeAction)
                        .ToImmutableArray()
                })
                .OrderBy(node => node.NodeId, StringComparer.Ordinal)
                .ToImmutableArray(),
            Transitions = document.Transitions
                .OrderBy(transition => transition.TransitionId, StringComparer.Ordinal)
                .ToImmutableArray(),
            BlockDependencies = document.BlockDependencies
                .OrderBy(dependency => dependency.BlockType, StringComparer.Ordinal)
                .ToImmutableArray()
        };
    }

    private static FlowIrAction NormalizeAction(FlowIrAction action)
    {
        if (action.OperationalPolicy is null)
        {
            return action;
        }

        return action with
        {
            OperationalPolicy = action.OperationalPolicy with
            {
                ResourceLocks = action.OperationalPolicy.ResourceLocks
                    .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal)
                    .ToImmutableArray(),
                EvidenceRequirements = action.OperationalPolicy.EvidenceRequirements
                    .OrderBy(requirement => requirement.EvidenceKind, StringComparer.Ordinal)
                    .ToImmutableArray(),
                AllowedStationModes = action.OperationalPolicy.AllowedStationModes
                    .OrderBy(mode => mode)
                    .ToImmutableArray()
            }
        };
    }

    private static ApplicationError? Validate(FlowIrDocument document)
    {
        if (!string.Equals(document.SchemaVersion, FlowIrSchema.Current, StringComparison.Ordinal))
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

        if (document.BlockDependencies.IsDefault)
        {
            return Invalid("Block dependencies collection is required.");
        }

        var dependencyTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in document.BlockDependencies)
        {
            if (dependency is null
                || !IsCanonicalValue(dependency.BlockType)
                || dependency.Version <= 0
                || !string.Equals(
                    dependency.ContractSchemaVersion,
                    RuntimeActionContractSchema.Current,
                    StringComparison.Ordinal)
                || !IsSha256(dependency.ContractSha256))
            {
                return Invalid("Every Blockly dependency must have a canonical type, positive version, supported contract schema, and SHA-256 hash.");
            }

            if (!dependencyTypes.Add(dependency.BlockType))
            {
                return Invalid($"Blockly dependency '{dependency.BlockType}' is duplicated.");
            }
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

        if (!document.Nodes.Any(node => node.Kind is FlowIrNodeKind.Command
                or FlowIrNodeKind.PythonScript
                or FlowIrNodeKind.Blockly))
        {
            return Invalid("Flow IR must contain at least one executable action node.");
        }

        var blockActionHashes = document.Nodes
            .Where(node => node.Kind == FlowIrNodeKind.Blockly)
            .SelectMany(node => node.Actions)
            .Select(action => action.Source.ContentHash)
            .ToHashSet(StringComparer.Ordinal);
        if (document.BlockDependencies.Any(dependency => !blockActionHashes.Contains(dependency.ContractSha256)))
        {
            return Invalid("Flow IR contains a Blockly dependency that is not used by a compiled Blockly action.");
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
        var isExecutable = node.Kind is FlowIrNodeKind.Command
            or FlowIrNodeKind.PythonScript
            or FlowIrNodeKind.Blockly;
        if (!isExecutable)
        {
            return node.Actions.Length == 0 && node.Source.ContentHash is null
                ? null
                : Invalid($"Routing node {node.NodeId} cannot contain actions or a content hash.");
        }

        if (node.Kind != FlowIrNodeKind.Blockly && node.Actions.Length != 1)
        {
            return Invalid($"Executable node {node.NodeId} must contain exactly one action in the Flow IR contract.");
        }

        if (node.Kind == FlowIrNodeKind.Blockly && node.Actions.IsDefaultOrEmpty)
        {
            return Invalid($"Blockly node {node.NodeId} must contain at least one statically compiled action.");
        }

        for (var index = 0; index < node.Actions.Length; index += 1)
        {
            var compiledAction = node.Actions[index];
            if (compiledAction is null
                || compiledAction.Target is null
                || compiledAction.Execution is null
                || compiledAction.Source is null
                || !string.Equals(compiledAction.ActionId, $"{node.NodeId}:action:{index + 1}", StringComparison.Ordinal)
                || !IsCanonicalValue(compiledAction.DisplayName)
                || !IsCanonicalValue(compiledAction.RequiredCapability)
                || !IsCanonicalValue(compiledAction.CommandName)
                || !Enum.IsDefined(compiledAction.Target.Kind)
                || !IsCanonicalValue(compiledAction.Target.Reference))
            {
                return Invalid($"Action {index + 1} on node {node.NodeId} has invalid identity, command, or target metadata.");
            }

            if (compiledAction.Execution.TimeoutMilliseconds <= 0
                || compiledAction.Execution.TimeoutMilliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond
                || compiledAction.Execution.RetryLimit is < 0 or > 10
                || compiledAction.Execution.CancellationMode != FlowIrCancellationMode.Cooperative)
            {
                return Invalid($"Action {compiledAction.ActionId} execution policy is not supported by the Flow IR contract.");
            }

            var operationalError = ValidateOperationalPolicy(compiledAction);
            if (operationalError is not null)
            {
                return operationalError;
            }
        }

        if (node.Kind == FlowIrNodeKind.Blockly)
        {
            return ValidateBlocklyNode(document, node);
        }

        var action = node.Actions[0];
        if (!string.Equals(action.DisplayName, node.DisplayName, StringComparison.Ordinal))
        {
            return Invalid($"Action on node {node.NodeId} does not match its executable node metadata.");
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
                                         && action.Source.ContentHash is null => null,
            FlowIrNodeKind.PythonScript when action.Kind == FlowIrActionKind.PythonScript
                                             && action.Target.Kind == FlowIrTargetReferenceKind.Capability
                                             && string.Equals(
                                                 action.Target.Reference,
                                                 action.RequiredCapability,
                                                 StringComparison.Ordinal)
                                             && string.Equals(
                                                 action.RequiredCapability,
                                                 RuntimeScriptCommand.PythonCapability,
                                                 StringComparison.Ordinal)
                                             && string.Equals(
                                                 action.CommandName,
                                                 RuntimeScriptCommand.PythonCommandName,
                                                 StringComparison.Ordinal) =>
                ValidatePythonAction(action),
            _ => Invalid($"Action {action.ActionId} kind does not match node kind {node.Kind}.")
        };
    }

    private static ApplicationError? ValidateBlocklyNode(FlowIrDocument document, FlowIrNode node)
    {
        if (!IsSha256(node.Source.ContentHash ?? string.Empty))
        {
            return Invalid($"Blockly node {node.NodeId} must carry its workspace SHA-256 hash.");
        }

        var dependencyHashes = document.BlockDependencies
            .Select(dependency => dependency.ContractSha256)
            .ToHashSet(StringComparer.Ordinal);
        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in node.Actions)
        {
            if (action.Kind != FlowIrActionKind.DeviceCommand
                || action.PythonScript is not null
                || action.Source.ElementKind != FlowIrSourceElementKind.BlocklyBlock
                || !IsCanonicalValue(action.Source.ElementId)
                || !blockIds.Add(action.Source.ElementId)
                || !IsSha256(action.Source.ContentHash ?? string.Empty)
                || !dependencyHashes.Contains(action.Source.ContentHash!))
            {
                return Invalid($"Blockly action {action.ActionId} has invalid static action or block source metadata.");
            }

            var sourceError = ValidateSource(
                document,
                action.Source,
                FlowIrSourceElementKind.BlocklyBlock,
                action.Source.ElementId);
            if (sourceError is not null)
            {
                return sourceError;
            }
        }

        return null;
    }

    private static ApplicationError? ValidateOperationalPolicy(FlowIrAction action)
    {
        var policy = action.OperationalPolicy;
        if (policy is null)
        {
            return action.Execution.RetryLimit == 0
                ? null
                : Invalid(
                    $"Legacy action {action.ActionId} cannot declare retries without an operational policy.");
        }

        if (!Enum.IsDefined(policy.IdempotencyClass)
            || !Enum.IsDefined(policy.RecoveryPolicy)
            || !Enum.IsDefined(policy.FailurePolicy)
            || policy.ResourceLocks.IsDefault
            || policy.EvidenceRequirements.IsDefault
            || policy.AllowedStationModes.IsDefaultOrEmpty)
        {
            return Invalid($"Action {action.ActionId} operational policy is incomplete.");
        }

        if (policy.RecoveryPolicy == FlowIrRecoveryPolicy.AutomaticReplay
            && policy.IdempotencyClass != FlowIrIdempotencyClass.Idempotent)
        {
            return Invalid(
                $"Action {action.ActionId} cannot automatically replay unless it is idempotent.");
        }

        if (policy.IdempotencyClass == FlowIrIdempotencyClass.NonIdempotent
            && policy.RecoveryPolicy is not FlowIrRecoveryPolicy.ManualAuthorization
                and not FlowIrRecoveryPolicy.NeverReplay)
        {
            return Invalid(
                $"Non-idempotent action {action.ActionId} requires manual authorization or no replay.");
        }

        if ((policy.FailurePolicy == FlowIrFailurePolicy.Retry)
            != (action.Execution.RetryLimit > 0))
        {
            return Invalid(
                $"Action {action.ActionId} retry limit and failure policy must be declared together.");
        }

        if (policy.FailurePolicy == FlowIrFailurePolicy.Retry
            && policy.IdempotencyClass != FlowIrIdempotencyClass.Idempotent)
        {
            return Invalid(
                $"Action {action.ActionId} cannot retry unless it is idempotent.");
        }

        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in policy.ResourceLocks)
        {
            if (resource is null
                || !IsCanonicalValue(resource.ResourceId)
                || !Enum.IsDefined(resource.Mode)
                || !resourceIds.Add(resource.ResourceId))
            {
                return Invalid($"Action {action.ActionId} contains invalid or duplicate resource locks.");
            }
        }

        var evidenceKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in policy.EvidenceRequirements)
        {
            if (evidence is null
                || !IsCanonicalValue(evidence.EvidenceKind)
                || evidence.MinimumCount <= 0
                || !evidenceKinds.Add(evidence.EvidenceKind))
            {
                return Invalid(
                    $"Action {action.ActionId} contains invalid or duplicate evidence requirements.");
            }
        }

        if (policy.AllowedStationModes.Any(mode => !Enum.IsDefined(mode))
            || policy.AllowedStationModes.Distinct().Count()
            != policy.AllowedStationModes.Length)
        {
            return Invalid(
                $"Action {action.ActionId} contains invalid or duplicate allowed station modes.");
        }

        return null;
    }

    private static ApplicationError? ValidatePythonAction(FlowIrAction action)
    {
        var script = action.PythonScript;
        if (script is null
            || !string.Equals(script.Language, "Python", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(script.SourceCode)
            || !IsCanonicalValue(script.SourceHash)
            || !IsSha256(script.SourceHash)
            || !IsCanonicalValue(script.Version)
            || !string.Equals(action.Source.ContentHash, script.SourceHash, StringComparison.Ordinal))
        {
            return Invalid($"Python action {action.ActionId} has invalid source metadata.");
        }

        var computedSourceHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(script.SourceCode)))
            .ToLowerInvariant();
        if (!string.Equals(script.SourceHash, computedSourceHash, StringComparison.Ordinal))
        {
            return Invalid($"Python action {action.ActionId} source hash does not match its UTF-8 source code.");
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
        writer.WritePropertyName("blockDependencies");
        writer.WriteStartArray();
        foreach (var dependency in document.BlockDependencies)
        {
            writer.WriteStartObject();
            writer.WriteString("blockType", dependency.BlockType);
            writer.WriteNumber("version", dependency.Version);
            writer.WriteString("contractSchemaVersion", dependency.ContractSchemaVersion);
            writer.WriteString("contractSha256", dependency.ContractSha256);
            writer.WriteEndObject();
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
        if (action.OperationalPolicy is not null)
        {
            writer.WritePropertyName("operationalPolicy");
            WriteOperationalPolicy(writer, action.OperationalPolicy);
        }

        writer.WritePropertyName("pythonScript");
        if (action.PythonScript is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WritePythonScript(writer, action.PythonScript);
        }

        writer.WritePropertyName("source");
        WriteSource(writer, action.Source);
        writer.WriteEndObject();
    }

    private static void WriteOperationalPolicy(
        Utf8JsonWriter writer,
        FlowIrOperationalPolicy policy)
    {
        writer.WriteStartObject();
        writer.WriteString("idempotencyClass", IdempotencyClass(policy.IdempotencyClass));
        writer.WriteString("recoveryPolicy", RecoveryPolicy(policy.RecoveryPolicy));
        writer.WriteString("failurePolicy", FailurePolicy(policy.FailurePolicy));
        writer.WritePropertyName("resourceLocks");
        writer.WriteStartArray();
        foreach (var resource in policy.ResourceLocks)
        {
            writer.WriteStartObject();
            writer.WriteString("resourceId", resource.ResourceId);
            writer.WriteString("mode", ResourceLockMode(resource.Mode));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("evidenceRequirements");
        writer.WriteStartArray();
        foreach (var evidence in policy.EvidenceRequirements)
        {
            writer.WriteStartObject();
            writer.WriteString("evidenceKind", evidence.EvidenceKind);
            writer.WriteNumber("minimumCount", evidence.MinimumCount);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("allowedStationModes");
        writer.WriteStartArray();
        foreach (var mode in policy.AllowedStationModes)
        {
            writer.WriteStringValue(StationMode(mode));
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePythonScript(Utf8JsonWriter writer, FlowIrPythonScript script)
    {
        writer.WriteStartObject();
        writer.WriteString("language", script.Language);
        writer.WriteString("sourceCode", script.SourceCode);
        writer.WriteString("sourceHash", script.SourceHash);
        writer.WriteString("version", script.Version);
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
        FlowIrNodeKind.Blockly => "blockly",
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
        FlowIrTargetReferenceKind.System => "system",
        FlowIrTargetReferenceKind.SlotGroup => "slotGroup",
        FlowIrTargetReferenceKind.Slot => "slot",
        FlowIrTargetReferenceKind.ProductionUnit => "productionUnit",
        FlowIrTargetReferenceKind.Capability => "capability",
        FlowIrTargetReferenceKind.Driver => "driver",
        _ => throw new InvalidOperationException($"Unsupported Flow IR target kind {value}.")
    };

    private static string CancellationMode(FlowIrCancellationMode value) => value switch
    {
        FlowIrCancellationMode.Cooperative => "cooperative",
        _ => throw new InvalidOperationException($"Unsupported Flow IR cancellation mode {value}.")
    };

    private static string IdempotencyClass(FlowIrIdempotencyClass value) => value switch
    {
        FlowIrIdempotencyClass.Idempotent => "idempotent",
        FlowIrIdempotencyClass.Conditional => "conditional",
        FlowIrIdempotencyClass.NonIdempotent => "nonIdempotent",
        _ => throw new InvalidOperationException(
            $"Unsupported Flow IR idempotency class {value}.")
    };

    private static string RecoveryPolicy(FlowIrRecoveryPolicy value) => value switch
    {
        FlowIrRecoveryPolicy.AutomaticReplay => "automaticReplay",
        FlowIrRecoveryPolicy.ResumeFromCheckpoint => "resumeFromCheckpoint",
        FlowIrRecoveryPolicy.ManualAuthorization => "manualAuthorization",
        FlowIrRecoveryPolicy.NeverReplay => "neverReplay",
        _ => throw new InvalidOperationException(
            $"Unsupported Flow IR recovery policy {value}.")
    };

    private static string FailurePolicy(FlowIrFailurePolicy value) => value switch
    {
        FlowIrFailurePolicy.Continue => "continue",
        FlowIrFailurePolicy.Skip => "skip",
        FlowIrFailurePolicy.Retry => "retry",
        FlowIrFailurePolicy.Rework => "rework",
        FlowIrFailurePolicy.ManualDisposition => "manualDisposition",
        FlowIrFailurePolicy.Hold => "hold",
        FlowIrFailurePolicy.Terminate => "terminate",
        _ => throw new InvalidOperationException(
            $"Unsupported Flow IR failure policy {value}.")
    };

    private static string ResourceLockMode(FlowIrResourceLockMode value) => value switch
    {
        FlowIrResourceLockMode.Shared => "shared",
        FlowIrResourceLockMode.Exclusive => "exclusive",
        _ => throw new InvalidOperationException(
            $"Unsupported Flow IR resource lock mode {value}.")
    };

    private static string StationMode(FlowIrStationMode value) => value switch
    {
        FlowIrStationMode.Automatic => "automatic",
        FlowIrStationMode.Manual => "manual",
        FlowIrStationMode.Setup => "setup",
        FlowIrStationMode.Maintenance => "maintenance",
        FlowIrStationMode.Simulation => "simulation",
        _ => throw new InvalidOperationException(
            $"Unsupported Flow IR station mode {value}.")
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
        FlowIrSourceElementKind.BlocklyBlock => "blocklyBlock",
        _ => throw new InvalidOperationException($"Unsupported Flow IR source element kind {value}.")
    };
}
