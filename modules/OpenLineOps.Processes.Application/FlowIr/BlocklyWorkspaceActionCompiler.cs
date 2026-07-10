using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Runtime.Application.Commands;

namespace OpenLineOps.Processes.Application.FlowIr;

internal static class BlocklyWorkspaceActionCompiler
{
    private const int CurrentBlocklyLanguageVersion = 0;
    private const int MaximumBlocks = 1_024;
    private const int MaximumWorkspaceUtf8Bytes = 1_048_576;
    private const long MaximumRuntimeTimeoutMilliseconds = uint.MaxValue - 1L;
    private const long MaximumWaitDurationMilliseconds = MaximumRuntimeTimeoutMilliseconds - 1_000L;
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    public static Result<CompiledBlocklyWorkspace> Compile(
        ProcessDefinition definition,
        ProcessNode node,
        IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails> catalog)
    {
        if (string.IsNullOrWhiteSpace(node.BlocklyWorkspaceJson))
        {
            return Failure("Processes.FlowIrBlocklyWorkspaceMissing", node, "Blockly workspace JSON is required.");
        }

        if (Encoding.UTF8.GetByteCount(node.BlocklyWorkspaceJson) > MaximumWorkspaceUtf8Bytes)
        {
            return Failure(
                "Processes.FlowIrBlocklyWorkspaceTooLarge",
                node,
                $"Blockly workspace cannot exceed {MaximumWorkspaceUtf8Bytes} UTF-8 bytes.");
        }

        JsonDocument workspace;
        try
        {
            workspace = JsonDocument.Parse(node.BlocklyWorkspaceJson);
        }
        catch (JsonException exception)
        {
            return Failure(
                "Processes.FlowIrBlocklyWorkspaceInvalid",
                node,
                $"Blockly workspace JSON is invalid: {exception.Message}");
        }

        using (workspace)
        {
            try
            {
                var definitions = BuildDefinitionIndex(catalog);
                var blocks = ParseWorkspace(workspace.RootElement);
                var actions = ImmutableArray.CreateBuilder<FlowIrAction>(blocks.Count);
                var dependencies = new Dictionary<string, FlowIrBlockDependency>(StringComparer.Ordinal);
                var actionContractSerializer = new RuntimeActionContractCanonicalSerializer();

                for (var index = 0; index < blocks.Count; index += 1)
                {
                    var block = blocks[index];
                    if (!definitions.TryGetValue(block.BlockType, out var definitionDetails))
                    {
                        return Failure(
                            "Processes.FlowIrBlocklyBlockUnknown",
                            node,
                            $"Blockly block '{block.BlockType}' is not present in the compiler catalog.");
                    }

                    if (!string.Equals(
                            definitionDetails.ExecutionMode,
                            ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
                            StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(definitionDetails.RuntimeActionContractJson)
                        || string.IsNullOrWhiteSpace(definitionDetails.RuntimeActionContractSha256)
                        || string.IsNullOrWhiteSpace(definitionDetails.RuntimeActionContractSchemaVersion))
                    {
                        return Failure(
                            "Processes.FlowIrBlocklyBlockNotDeclarative",
                            node,
                            $"Blockly block '{block.BlockType}' does not provide a declarative Runtime Action Contract.");
                    }

                    var contractResult = actionContractSerializer.Deserialize(
                        definitionDetails.RuntimeActionContractJson);
                    if (contractResult.IsFailure)
                    {
                        return Failure(
                            "Processes.FlowIrBlocklyContractInvalid",
                            node,
                            $"Blockly block '{block.BlockType}' contract is invalid: {contractResult.Error.Message}");
                    }

                    var contractArtifact = actionContractSerializer.Serialize(contractResult.Value);
                    if (contractArtifact.IsFailure
                        || !string.Equals(
                            contractArtifact.Value.SchemaVersion,
                            definitionDetails.RuntimeActionContractSchemaVersion,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            contractArtifact.Value.Sha256,
                            definitionDetails.RuntimeActionContractSha256,
                            StringComparison.Ordinal))
                    {
                        return Failure(
                            "Processes.FlowIrBlocklyContractHashMismatch",
                            node,
                            $"Blockly block '{block.BlockType}' contract metadata does not match its canonical contract.");
                    }

                    var fieldsResult = ValidateFields(block, contractResult.Value.Fields);
                    if (fieldsResult.IsFailure)
                    {
                        return Failure(fieldsResult.Error.Code, node, fieldsResult.Error.Message);
                    }

                    var source = new FlowIrSourceTrace(
                        definition.Id.Value,
                        definition.VersionId.Value,
                        FlowIrSourceElementKind.BlocklyBlock,
                        block.BlockId,
                        contractArtifact.Value.Sha256);
                    var actionResult = CompileAction(
                        node,
                        block,
                        definitionDetails,
                        contractResult.Value,
                        fieldsResult.Value,
                        index,
                        source);
                    if (actionResult.IsFailure)
                    {
                        return Failure(actionResult.Error.Code, node, actionResult.Error.Message);
                    }

                    actions.Add(actionResult.Value);
                    dependencies[definitionDetails.BlockType] = new FlowIrBlockDependency(
                        definitionDetails.BlockType,
                        definitionDetails.Version,
                        contractArtifact.Value.SchemaVersion,
                        contractArtifact.Value.Sha256);
                }

                var workspaceSha256 = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(node.BlocklyWorkspaceJson)))
                    .ToLowerInvariant();
                return Result.Success(new CompiledBlocklyWorkspace(
                    actions.ToImmutable(),
                    dependencies.Values
                        .OrderBy(dependency => dependency.BlockType, StringComparer.Ordinal)
                        .ToImmutableArray(),
                    workspaceSha256));
            }
            catch (InvalidDataException exception)
            {
                return Failure("Processes.FlowIrBlocklyWorkspaceInvalid", node, exception.Message);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                return Failure("Processes.FlowIrBlocklyFieldInvalid", node, exception.Message);
            }
        }
    }

    private static Dictionary<string, ProcessBlocklyBlockDefinitionDetails> BuildDefinitionIndex(
        IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails> catalog)
    {
        var definitions = new Dictionary<string, ProcessBlocklyBlockDefinitionDetails>(StringComparer.Ordinal);
        foreach (var definition in catalog
                     .OrderBy(candidate => candidate.BlockType, StringComparer.Ordinal)
                     .ThenByDescending(candidate => candidate.Version))
        {
            if (string.IsNullOrWhiteSpace(definition.BlockType) || definition.Version <= 0)
            {
                throw new InvalidDataException("Blockly compiler catalog contains an invalid block identity.");
            }

            definitions.TryAdd(definition.BlockType, definition);
        }

        return definitions;
    }

    private static List<SerializedBlocklyBlock> ParseWorkspace(JsonElement root)
    {
        RequireObject(root, "$");
        EnsureProperties(root, "$", "blocks");
        var blocksContainer = RequiredProperty(root, "blocks", "$", JsonValueKind.Object);
        EnsureProperties(blocksContainer, "$.blocks", "languageVersion", "blocks");
        var languageVersion = RequiredProperty(
            blocksContainer,
            "languageVersion",
            "$.blocks",
            JsonValueKind.Number);
        if (!languageVersion.TryGetInt32(out var version) || version != CurrentBlocklyLanguageVersion)
        {
            throw new InvalidDataException(
                $"Blockly workspace languageVersion must be exactly {CurrentBlocklyLanguageVersion}.");
        }

        var topLevelBlocks = RequiredProperty(
            blocksContainer,
            "blocks",
            "$.blocks",
            JsonValueKind.Array);
        if (topLevelBlocks.GetArrayLength() != 1)
        {
            throw new InvalidDataException(
                "Blockly workspace must contain exactly one connected top-level block chain.");
        }

        var result = new List<SerializedBlocklyBlock>();
        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        ParseBlock(topLevelBlocks[0], "$.blocks.blocks[0]", result, blockIds);
        return result;
    }

    private static void ParseBlock(
        JsonElement element,
        string path,
        ICollection<SerializedBlocklyBlock> blocks,
        ISet<string> blockIds)
    {
        if (blocks.Count >= MaximumBlocks)
        {
            throw new InvalidDataException($"Blockly workspace cannot contain more than {MaximumBlocks} blocks.");
        }

        RequireObject(element, path);
        EnsureProperties(element, path, "type", "id", "x", "y", "fields", "next");
        var blockType = RequiredString(element, "type", path);
        var blockId = RequiredString(element, "id", path);
        if (!blockIds.Add(blockId))
        {
            throw new InvalidDataException($"Blockly workspace contains duplicate block id '{blockId}'.");
        }

        if (element.TryGetProperty("x", out var x) && x.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"{path}.x must be a number.");
        }

        if (element.TryGetProperty("y", out var y) && y.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"{path}.y must be a number.");
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (element.TryGetProperty("fields", out var fieldsElement))
        {
            if (fieldsElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{path}.fields must be an object when present.");
            }

            foreach (var field in fieldsElement.EnumerateObject())
            {
                if (!fields.TryAdd(field.Name, field.Value.Clone()))
                {
                    throw new InvalidDataException($"{path}.fields contains duplicate field '{field.Name}'.");
                }
            }
        }

        blocks.Add(new SerializedBlocklyBlock(blockId, blockType, fields));
        if (!element.TryGetProperty("next", out var nextElement))
        {
            return;
        }

        RequireObject(nextElement, $"{path}.next");
        EnsureProperties(nextElement, $"{path}.next", "block");
        ParseBlock(
            RequiredProperty(nextElement, "block", $"{path}.next", JsonValueKind.Object),
            $"{path}.next.block",
            blocks,
            blockIds);
    }

    private static Result<IReadOnlyDictionary<string, JsonElement>> ValidateFields(
        SerializedBlocklyBlock block,
        IReadOnlyDictionary<string, RuntimeActionFieldDefinition> definitions)
    {
        var normalizedFields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var fieldName in block.Fields.Keys.Order(StringComparer.Ordinal))
        {
            if (!definitions.ContainsKey(fieldName))
            {
                return FieldFailure(block, $"contains unknown field '{fieldName}'.");
            }
        }

        foreach (var (fieldName, definition) in definitions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!block.Fields.TryGetValue(fieldName, out var value))
            {
                if (definition.Required)
                {
                    return FieldFailure(block, $"is missing required field '{fieldName}'.");
                }

                continue;
            }

            var validationError = ValidateFieldValue(fieldName, value, definition);
            if (validationError is not null)
            {
                return FieldFailure(block, validationError);
            }

            normalizedFields[fieldName] = definition.Type == RuntimeActionFieldType.Boolean
                ? JsonSerializer.SerializeToElement(
                    string.Equals(value.GetString(), "TRUE", StringComparison.Ordinal))
                : value.Clone();
        }

        return Result.Success<IReadOnlyDictionary<string, JsonElement>>(normalizedFields);
    }

    private static string? ValidateFieldValue(
        string fieldName,
        JsonElement value,
        RuntimeActionFieldDefinition definition)
    {
        decimal? numericValue = null;
        string? stringValue = null;
        switch (definition.Type)
        {
            case RuntimeActionFieldType.Text:
            case RuntimeActionFieldType.TargetReference:
                if (value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(stringValue = value.GetString()))
                {
                    return $"field '{fieldName}' must be a non-empty string.";
                }

                if (!string.Equals(stringValue, stringValue.Trim(), StringComparison.Ordinal))
                {
                    return $"field '{fieldName}' must be canonical without surrounding whitespace.";
                }

                break;
            case RuntimeActionFieldType.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
                {
                    return $"field '{fieldName}' must be a decimal JSON number.";
                }

                numericValue = number;
                break;
            case RuntimeActionFieldType.WholeNumber:
                if (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetDecimal(out var integer)
                    || integer != decimal.Truncate(integer))
                {
                    return $"field '{fieldName}' must be a whole JSON number.";
                }

                numericValue = integer;
                break;
            case RuntimeActionFieldType.Boolean:
                if (value.ValueKind != JsonValueKind.String
                    || value.GetString() is not ("TRUE" or "FALSE"))
                {
                    return $"field '{fieldName}' must be the current Blockly checkbox value TRUE or FALSE.";
                }

                break;
            case RuntimeActionFieldType.Json:
                break;
            default:
                return $"field '{fieldName}' has unsupported type {definition.Type}.";
        }

        if (stringValue is not null)
        {
            if (definition.MaxLength is not null && stringValue.Length > definition.MaxLength.Value)
            {
                return $"field '{fieldName}' exceeds maxLength {definition.MaxLength.Value}.";
            }

            if (definition.AllowedValues is not null
                && !definition.AllowedValues.Contains(stringValue, StringComparer.Ordinal))
            {
                return $"field '{fieldName}' value '{stringValue}' is not allowed.";
            }
        }

        if (numericValue is not null
            && (numericValue < definition.Minimum || numericValue > definition.Maximum))
        {
            return $"field '{fieldName}' is outside its declared numeric range.";
        }

        return null;
    }

    private static Result<FlowIrAction> CompileAction(
        ProcessNode node,
        SerializedBlocklyBlock block,
        ProcessBlocklyBlockDefinitionDetails definition,
        RuntimeActionContract contract,
        IReadOnlyDictionary<string, JsonElement> fields,
        int index,
        FlowIrSourceTrace source)
    {
        var actionId = $"{node.Id.Value}:action:{index + 1}";
        return contract.Emit switch
        {
            RuntimeDeviceCommandEmit command => CompileDeviceCommand(
                node,
                block,
                definition,
                command,
                fields,
                actionId,
                source),
            RuntimeDelayEmit delay => CompileDelay(
                node,
                block,
                definition,
                delay,
                fields,
                actionId,
                source),
            RuntimeResultPatchEmit resultPatch => CompileResultPatch(
                node,
                block,
                definition,
                resultPatch,
                fields,
                actionId,
                source),
            _ => Result.Failure<FlowIrAction>(ApplicationError.Validation(
                "Processes.FlowIrBlocklyEmitUnsupported",
                $"Blockly block '{block.BlockType}' emit type {contract.Emit.GetType().Name} is not supported."))
        };
    }

    private static Result<FlowIrAction> CompileDeviceCommand(
        ProcessNode node,
        SerializedBlocklyBlock block,
        ProcessBlocklyBlockDefinitionDetails definition,
        RuntimeDeviceCommandEmit command,
        IReadOnlyDictionary<string, JsonElement> fields,
        string actionId,
        FlowIrSourceTrace source)
    {
        var targetKindResult = ResolveString(command.TargetKind, fields, "targetKind", allowContext: false);
        if (targetKindResult.IsFailure)
        {
            return Result.Failure<FlowIrAction>(targetKindResult.Error);
        }

        var targetIdResult = ResolveString(command.TargetId, fields, "targetId", allowContext: false);
        if (targetIdResult.IsFailure)
        {
            return Result.Failure<FlowIrAction>(targetIdResult.Error);
        }

        if (!TryParseTargetKind(targetKindResult.Value, out var targetKind))
        {
            return Result.Failure<FlowIrAction>(ApplicationError.Validation(
                "Processes.FlowIrBlocklyTargetKindInvalid",
                $"Blockly block '{block.BlockType}' target kind '{targetKindResult.Value}' is not supported."));
        }

        var capabilityResult = ResolveString(command.Capability, fields, "capability", allowContext: false);
        var commandNameResult = ResolveString(command.CommandName, fields, "commandName", allowContext: false);
        if (capabilityResult.IsFailure || commandNameResult.IsFailure)
        {
            return Result.Failure<FlowIrAction>(capabilityResult.IsFailure
                ? capabilityResult.Error
                : commandNameResult.Error);
        }

        if (!IsCommandIdentifier(capabilityResult.Value) || !IsCommandIdentifier(commandNameResult.Value))
        {
            return Result.Failure<FlowIrAction>(ApplicationError.Validation(
                "Processes.FlowIrBlocklyCommandInvalid",
                $"Blockly block '{block.BlockType}' capability and command must be canonical identifiers."));
        }

        var inputResult = ResolveExpression(command.Input, fields, allowContext: false);
        if (inputResult.IsFailure)
        {
            return Result.Failure<FlowIrAction>(inputResult.Error);
        }

        var timeoutResult = ResolvePositiveWholeMilliseconds(command.TimeoutMilliseconds, fields);
        if (timeoutResult.IsFailure)
        {
            return Result.Failure<FlowIrAction>(timeoutResult.Error);
        }

        var payload = WithTarget(inputResult.Value, targetKindResult.Value, targetIdResult.Value);
        return Result.Success(CreateCommandAction(
            actionId,
            definition.DisplayName,
            capabilityResult.Value,
            commandNameResult.Value,
            targetKind,
            targetIdResult.Value,
            payload,
            timeoutResult.Value,
            command.RetryLimit,
            source));
    }

    private static Result<FlowIrAction> CompileDelay(
        ProcessNode node,
        SerializedBlocklyBlock block,
        ProcessBlocklyBlockDefinitionDetails definition,
        RuntimeDelayEmit delay,
        IReadOnlyDictionary<string, JsonElement> fields,
        string actionId,
        FlowIrSourceTrace source)
    {
        var durationResult = ResolveNonNegativeWholeMilliseconds(delay.DurationMilliseconds, fields);
        if (durationResult.IsFailure)
        {
            return Result.Failure<FlowIrAction>(durationResult.Error);
        }

        var payload = new JsonObject
        {
            ["durationMilliseconds"] = durationResult.Value
        }.ToJsonString(CompactJsonOptions);
        var timeout = Math.Max(1L, checked(durationResult.Value + 1_000L));
        return Result.Success(CreateCommandAction(
            actionId,
            definition.DisplayName,
            RuntimeFlowCommand.Capability,
            RuntimeFlowCommand.WaitCommandName,
            FlowIrTargetReferenceKind.System,
            "runtime.flow",
            payload,
            timeout,
            retryLimit: 0,
            source));
    }

    private static Result<FlowIrAction> CompileResultPatch(
        ProcessNode node,
        SerializedBlocklyBlock block,
        ProcessBlocklyBlockDefinitionDetails definition,
        RuntimeResultPatchEmit resultPatch,
        IReadOnlyDictionary<string, JsonElement> fields,
        string actionId,
        FlowIrSourceTrace source)
    {
        var assignments = new JsonArray();
        foreach (var assignment in resultPatch.Assignments)
        {
            if (assignment.When is not null)
            {
                if (!fields.TryGetValue(assignment.When.FieldName, out var condition)
                    || condition.ValueKind is not JsonValueKind.True and not JsonValueKind.False
                    || condition.GetBoolean() != assignment.When.ExpectedValue)
                {
                    continue;
                }
            }

            var keyResult = ResolveString(assignment.Key, fields, "resultPatch.key", allowContext: false);
            if (keyResult.IsFailure)
            {
                return Result.Failure<FlowIrAction>(keyResult.Error);
            }

            var valueResult = ResolveExpression(
                assignment.Value,
                fields,
                allowContext: true,
                contextNode: node);
            if (valueResult.IsFailure)
            {
                return Result.Failure<FlowIrAction>(valueResult.Error);
            }

            assignments.Add(new JsonObject
            {
                ["key"] = keyResult.Value,
                ["value"] = JsonNode.Parse(valueResult.Value.GetRawText())
            });
        }

        var payload = new JsonObject
        {
            ["assignments"] = assignments
        }.ToJsonString(CompactJsonOptions);
        return Result.Success(CreateCommandAction(
            actionId,
            definition.DisplayName,
            RuntimeFlowCommand.Capability,
            RuntimeFlowCommand.ResultPatchCommandName,
            FlowIrTargetReferenceKind.System,
            "runtime.flow",
            payload,
            timeoutMilliseconds: 5_000,
            retryLimit: 0,
            source));
    }

    private static FlowIrAction CreateCommandAction(
        string actionId,
        string displayName,
        string capability,
        string commandName,
        FlowIrTargetReferenceKind targetKind,
        string targetId,
        string inputPayload,
        long timeoutMilliseconds,
        int retryLimit,
        FlowIrSourceTrace source)
    {
        return new FlowIrAction(
            actionId,
            FlowIrActionKind.DeviceCommand,
            displayName,
            capability,
            commandName,
            new FlowIrTargetReference(targetKind, targetId),
            inputPayload,
            new FlowIrExecutionPolicy(
                timeoutMilliseconds,
                retryLimit,
                FlowIrCancellationMode.Cooperative),
            PythonScript: null,
            source);
    }

    private static Result<long> ResolvePositiveWholeMilliseconds(
        RuntimeActionValueExpression expression,
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        var value = ResolveWholeNumber(expression, fields, "timeoutMilliseconds");
        if (value.IsFailure
            || value.Value <= 0
            || value.Value > MaximumRuntimeTimeoutMilliseconds)
        {
            return Result.Failure<long>(value.IsFailure
                ? value.Error
                : ApplicationError.Validation(
                    "Processes.FlowIrBlocklyTimeoutInvalid",
                    $"Compiled timeoutMilliseconds must be between 1 and {MaximumRuntimeTimeoutMilliseconds}."));
        }

        return value;
    }

    private static Result<long> ResolveNonNegativeWholeMilliseconds(
        RuntimeActionValueExpression expression,
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        var value = ResolveWholeNumber(expression, fields, "durationMilliseconds");
        if (value.IsFailure
            || value.Value < 0
            || value.Value > MaximumWaitDurationMilliseconds)
        {
            return Result.Failure<long>(value.IsFailure
                ? value.Error
                : ApplicationError.Validation(
                    "Processes.FlowIrBlocklyDurationInvalid",
                    $"Compiled durationMilliseconds must be between 0 and {MaximumWaitDurationMilliseconds}."));
        }

        return value;
    }

    private static Result<long> ResolveWholeNumber(
        RuntimeActionValueExpression expression,
        IReadOnlyDictionary<string, JsonElement> fields,
        string path)
    {
        var resolved = ResolveExpression(expression, fields, allowContext: false);
        if (resolved.IsFailure
            || resolved.Value.ValueKind != JsonValueKind.Number
            || !resolved.Value.TryGetInt64(out var value))
        {
            return Result.Failure<long>(resolved.IsFailure
                ? resolved.Error
                : ApplicationError.Validation(
                    "Processes.FlowIrBlocklyNumberInvalid",
                    $"Compiled {path} must be a 64-bit whole number."));
        }

        return Result.Success(value);
    }

    private static Result<string> ResolveString(
        RuntimeActionValueExpression expression,
        IReadOnlyDictionary<string, JsonElement> fields,
        string path,
        bool allowContext)
    {
        var resolved = ResolveExpression(expression, fields, allowContext);
        if (resolved.IsFailure
            || resolved.Value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(resolved.Value.GetString()))
        {
            return Result.Failure<string>(resolved.IsFailure
                ? resolved.Error
                : ApplicationError.Validation(
                    "Processes.FlowIrBlocklyStringInvalid",
                    $"Compiled {path} must be a non-empty string."));
        }

        return Result.Success(resolved.Value.GetString()!);
    }

    private static Result<JsonElement> ResolveExpression(
        RuntimeActionValueExpression expression,
        IReadOnlyDictionary<string, JsonElement> fields,
        bool allowContext,
        ProcessNode? contextNode = null)
    {
        switch (expression)
        {
            case RuntimeActionLiteralValue literal:
                return Result.Success(literal.Value.Clone());
            case RuntimeActionFieldValue field:
                return fields.TryGetValue(field.FieldName, out var value)
                    ? Result.Success(value.Clone())
                    : ExpressionFailure($"Field '{field.FieldName}' was not provided by the Blockly block.");
            case RuntimeActionContextValue { Context: RuntimeActionContextValueKind.NodeId }
                when allowContext && contextNode is not null:
                return Result.Success(JsonSerializer.SerializeToElement(contextNode.Id.Value));
            case RuntimeActionContextValue { Context: RuntimeActionContextValueKind.InputPayload }
                when allowContext && contextNode is not null:
                return Result.Success(JsonSerializer.SerializeToElement(contextNode.InputPayload));
            case RuntimeActionContextValue { Context: RuntimeActionContextValueKind.TimestampUtc }
                when allowContext:
                return Result.Success(JsonSerializer.SerializeToElement(new Dictionary<string, string>
                {
                    ["$context"] = "timestampUtc"
                }));
            case RuntimeActionContextValue:
                return ExpressionFailure("Runtime context expressions are not allowed for this static action value.");
            case RuntimeActionObjectValue objectValue:
            {
                var result = new JsonObject();
                foreach (var (name, childExpression) in objectValue.Properties
                             .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var child = ResolveExpression(childExpression, fields, allowContext, contextNode);
                    if (child.IsFailure)
                    {
                        return Result.Failure<JsonElement>(child.Error);
                    }

                    result[name] = JsonNode.Parse(child.Value.GetRawText());
                }

                return Result.Success(JsonSerializer.SerializeToElement(result));
            }
            case RuntimeActionArrayValue arrayValue:
            {
                var result = new JsonArray();
                foreach (var childExpression in arrayValue.Items)
                {
                    var child = ResolveExpression(childExpression, fields, allowContext, contextNode);
                    if (child.IsFailure)
                    {
                        return Result.Failure<JsonElement>(child.Error);
                    }

                    result.Add(JsonNode.Parse(child.Value.GetRawText()));
                }

                return Result.Success(JsonSerializer.SerializeToElement(result));
            }
            default:
                return ExpressionFailure($"Expression type {expression.GetType().Name} is not supported.");
        }
    }

    private static string WithTarget(JsonElement input, string targetKind, string targetId)
    {
        var payload = new JsonObject();
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in input.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }
        else
        {
            payload["input"] = JsonNode.Parse(input.GetRawText());
        }

        payload["targetKind"] = targetKind;
        payload["targetId"] = targetId;

        return payload.ToJsonString(CompactJsonOptions);
    }

    private static bool TryParseTargetKind(string value, out FlowIrTargetReferenceKind kind)
    {
        kind = value switch
        {
            RuntimeActionTargetKinds.System => FlowIrTargetReferenceKind.System,
            RuntimeActionTargetKinds.SlotGroup => FlowIrTargetReferenceKind.SlotGroup,
            RuntimeActionTargetKinds.Slot => FlowIrTargetReferenceKind.Slot,
            RuntimeActionTargetKinds.Dut => FlowIrTargetReferenceKind.Dut,
            RuntimeActionTargetKinds.Capability => FlowIrTargetReferenceKind.Capability,
            RuntimeActionTargetKinds.Driver => FlowIrTargetReferenceKind.Driver,
            _ => default
        };
        return RuntimeActionTargetKinds.All.Contains(value, StringComparer.Ordinal);
    }

    private static bool IsCommandIdentifier(string value)
    {
        return value.Length <= 256
            && IsAsciiLetter(value[0])
            && value.All(character => IsAsciiLetter(character)
                || character is >= '0' and <= '9'
                || character is '_' or '.' or ':' or '-');
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static void EnsureProperties(JsonElement element, string path, params string[] allowed)
    {
        var allowedNames = allowed.ToHashSet(StringComparer.Ordinal);
        var actualNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!actualNames.Add(property.Name))
            {
                throw new InvalidDataException($"{path} contains duplicate property '{property.Name}'.");
            }

            if (!allowedNames.Contains(property.Name))
            {
                throw new InvalidDataException($"{path} contains unknown property '{property.Name}'.");
            }
        }
    }

    private static JsonElement RequiredProperty(
        JsonElement element,
        string name,
        string path,
        JsonValueKind kind)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != kind)
        {
            throw new InvalidDataException($"{path}.{name} must be a {kind.ToString().ToLowerInvariant()}.");
        }

        return value;
    }

    private static string RequiredString(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || !string.Equals(value.GetString(), value.GetString()!.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{path}.{name} must be a canonical non-empty string.");
        }

        return value.GetString()!;
    }

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path} must be an object.");
        }
    }

    private static Result<T> ExpressionFailure<T>(string message)
    {
        return Result.Failure<T>(ApplicationError.Validation(
            "Processes.FlowIrBlocklyExpressionInvalid",
            message));
    }

    private static Result<JsonElement> ExpressionFailure(string message) =>
        ExpressionFailure<JsonElement>(message);

    private static Result<IReadOnlyDictionary<string, JsonElement>> FieldFailure(
        SerializedBlocklyBlock block,
        string message)
    {
        return Result.Failure<IReadOnlyDictionary<string, JsonElement>>(ApplicationError.Validation(
            "Processes.FlowIrBlocklyFieldInvalid",
            $"Blockly block '{block.BlockType}' ({block.BlockId}) {message}"));
    }

    private static Result<CompiledBlocklyWorkspace> Failure(
        string code,
        ProcessNode node,
        string message)
    {
        return Result.Failure<CompiledBlocklyWorkspace>(ApplicationError.Validation(
            code,
            $"Blockly process node {node.Id}: {message}"));
    }

    private sealed record SerializedBlocklyBlock(
        string BlockId,
        string BlockType,
        IReadOnlyDictionary<string, JsonElement> Fields);
}

internal sealed record CompiledBlocklyWorkspace(
    ImmutableArray<FlowIrAction> Actions,
    ImmutableArray<FlowIrBlockDependency> Dependencies,
    string WorkspaceSha256);
