using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Processes.Application.Scripting;

public sealed class RuntimeActionContractCanonicalSerializer
{
    private const int DefaultMaximumCanonicalJsonBytes = 1_048_576;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };

    private readonly int _maximumCanonicalJsonBytes;

    public RuntimeActionContractCanonicalSerializer(
        int maximumCanonicalJsonBytes = DefaultMaximumCanonicalJsonBytes)
    {
        _maximumCanonicalJsonBytes = maximumCanonicalJsonBytes > 0
            ? maximumCanonicalJsonBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumCanonicalJsonBytes));
    }

    public Result<RuntimeActionContractCanonicalArtifact> Serialize(RuntimeActionContract? contract)
    {
        var validation = RuntimeActionContractValidator.Validate(contract);
        if (validation.IsFailure)
        {
            return Result.Failure<RuntimeActionContractCanonicalArtifact>(validation.Error);
        }

        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
            {
                WriteContract(writer, validation.Value);
            }

            var bytes = buffer.WrittenSpan;
            if (bytes.Length > _maximumCanonicalJsonBytes)
            {
                return Failure<RuntimeActionContractCanonicalArtifact>(
                    $"Canonical contract JSON exceeds {_maximumCanonicalJsonBytes} UTF-8 bytes.");
            }

            return Result.Success(new RuntimeActionContractCanonicalArtifact(
                RuntimeActionContractSchemaVersions.V1,
                Encoding.UTF8.GetString(bytes),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or ArgumentException
                                           or FormatException
                                           or OverflowException)
        {
            return Failure<RuntimeActionContractCanonicalArtifact>(
                $"Contract could not be serialized canonically: {exception.Message}");
        }
    }

    public Result<RuntimeActionContract> Deserialize(string canonicalJson)
    {
        var parsed = Parse(canonicalJson);
        if (parsed.IsFailure)
        {
            return parsed;
        }

        var artifact = Serialize(parsed.Value);
        if (artifact.IsFailure)
        {
            return Result.Failure<RuntimeActionContract>(artifact.Error);
        }

        if (!string.Equals(canonicalJson, artifact.Value.CanonicalJson, StringComparison.Ordinal))
        {
            return Failure<RuntimeActionContract>(
                "Contract JSON is valid but is not in canonical form.");
        }

        return parsed;
    }

    public Result<RuntimeActionContract> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Failure<RuntimeActionContract>("Contract JSON is required.");
        }

        if (Encoding.UTF8.GetByteCount(json) > _maximumCanonicalJsonBytes)
        {
            return Failure<RuntimeActionContract>(
                $"Contract JSON exceeds {_maximumCanonicalJsonBytes} UTF-8 bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            var duplicatePath = FindDuplicateProperty(document.RootElement, "$", depth: 0);
            if (duplicatePath is not null)
            {
                return Failure<RuntimeActionContract>(
                    $"Contract contains duplicate property at {duplicatePath}.");
            }

            var contract = ReadContract(document.RootElement);
            var validation = RuntimeActionContractValidator.Validate(contract);
            if (validation.IsFailure)
            {
                return validation;
            }

            return Result.Success(validation.Value);
        }
        catch (ContractFormatException exception)
        {
            return Failure<RuntimeActionContract>(exception.Message);
        }
        catch (Exception exception) when (exception is JsonException
                                           or InvalidOperationException
                                           or FormatException
                                           or OverflowException)
        {
            return Failure<RuntimeActionContract>(
                $"Contract JSON is invalid: {exception.Message}");
        }
    }

    private static RuntimeActionContract ReadContract(JsonElement root)
    {
        RequireKind(root, JsonValueKind.Object, "$", "object");
        EnsureProperties(root, "$", "schemaVersion", "actionType", "fields", "emit");

        var fieldsElement = RequiredProperty(root, "fields", "$", JsonValueKind.Object);
        var fields = new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal);
        foreach (var property in fieldsElement.EnumerateObject())
        {
            fields.Add(property.Name, ReadFieldDefinition(property.Value, $"$.fields.{property.Name}"));
        }

        return new RuntimeActionContract(
            RequiredString(root, "schemaVersion", "$"),
            RequiredString(root, "actionType", "$"),
            fields,
            ReadEmit(RequiredProperty(root, "emit", "$", JsonValueKind.Object), "$.emit"));
    }

    private static RuntimeActionFieldDefinition ReadFieldDefinition(JsonElement element, string path)
    {
        RequireKind(element, JsonValueKind.Object, path, "object");
        EnsureProperties(element, path, "type", "required", "enum", "minimum", "maximum", "maxLength");

        IReadOnlyCollection<string>? allowedValues = null;
        if (element.TryGetProperty("enum", out var enumElement))
        {
            RequireKind(enumElement, JsonValueKind.Array, $"{path}.enum", "array");
            allowedValues = enumElement
                .EnumerateArray()
                .Select((value, index) => RequiredStringValue(value, $"{path}.enum[{index}]"))
                .ToArray();
        }

        return new RuntimeActionFieldDefinition(
            ReadFieldType(RequiredString(element, "type", path), $"{path}.type"),
            RequiredBoolean(element, "required", path),
            allowedValues,
            OptionalDecimal(element, "minimum", path),
            OptionalDecimal(element, "maximum", path),
            OptionalInt32(element, "maxLength", path));
    }

    private static RuntimeActionEmit ReadEmit(JsonElement element, string path)
    {
        var kind = RequiredString(element, "kind", path);
        return kind switch
        {
            "deviceCommand" => ReadDeviceCommand(element, path),
            "delay" => ReadDelay(element, path),
            "resultPatch" => ReadResultPatch(element, path),
            _ => throw Invalid($"{path}.kind '{kind}' is not supported.")
        };
    }

    private static RuntimeDeviceCommandEmit ReadDeviceCommand(JsonElement element, string path)
    {
        EnsureProperties(
            element,
            path,
            "kind",
            "targetKind",
            "targetId",
            "capability",
            "commandName",
            "input",
            "timeoutMilliseconds",
            "retryLimit");

        return new RuntimeDeviceCommandEmit(
            ReadValueExpression(
                RequiredProperty(element, "targetKind", path, JsonValueKind.Object),
                $"{path}.targetKind"),
            ReadValueExpression(
                RequiredProperty(element, "targetId", path, JsonValueKind.Object),
                $"{path}.targetId"),
            ReadValueExpression(
                RequiredProperty(element, "capability", path, JsonValueKind.Object),
                $"{path}.capability"),
            ReadValueExpression(
                RequiredProperty(element, "commandName", path, JsonValueKind.Object),
                $"{path}.commandName"),
            ReadValueExpression(RequiredProperty(element, "input", path, JsonValueKind.Object), $"{path}.input"),
            ReadValueExpression(
                RequiredProperty(element, "timeoutMilliseconds", path, JsonValueKind.Object),
                $"{path}.timeoutMilliseconds"),
            RequiredInt32(element, "retryLimit", path));
    }

    private static RuntimeDelayEmit ReadDelay(JsonElement element, string path)
    {
        EnsureProperties(element, path, "kind", "durationMilliseconds");
        return new RuntimeDelayEmit(ReadValueExpression(
            RequiredProperty(element, "durationMilliseconds", path, JsonValueKind.Object),
            $"{path}.durationMilliseconds"));
    }

    private static RuntimeResultPatchEmit ReadResultPatch(JsonElement element, string path)
    {
        EnsureProperties(element, path, "kind", "assignments");
        var assignmentsElement = RequiredProperty(element, "assignments", path, JsonValueKind.Array);
        var assignments = new List<RuntimeResultPatchAssignment>();
        var index = 0;
        foreach (var assignmentElement in assignmentsElement.EnumerateArray())
        {
            var assignmentPath = $"{path}.assignments[{index}]";
            RequireKind(assignmentElement, JsonValueKind.Object, assignmentPath, "object");
            EnsureProperties(assignmentElement, assignmentPath, "key", "value", "when");

            RuntimeActionFieldEqualsCondition? condition = null;
            if (assignmentElement.TryGetProperty("when", out var conditionElement))
            {
                RequireKind(conditionElement, JsonValueKind.Object, $"{assignmentPath}.when", "object");
                EnsureProperties(conditionElement, $"{assignmentPath}.when", "field", "equals");
                condition = new RuntimeActionFieldEqualsCondition(
                    RequiredString(conditionElement, "field", $"{assignmentPath}.when"),
                    RequiredBoolean(conditionElement, "equals", $"{assignmentPath}.when"));
            }

            assignments.Add(new RuntimeResultPatchAssignment(
                ReadValueExpression(
                    RequiredProperty(assignmentElement, "key", assignmentPath, JsonValueKind.Object),
                    $"{assignmentPath}.key"),
                ReadValueExpression(
                    RequiredProperty(assignmentElement, "value", assignmentPath, JsonValueKind.Object),
                    $"{assignmentPath}.value"),
                condition));
            index += 1;
        }

        return new RuntimeResultPatchEmit(assignments);
    }

    private static RuntimeActionValueExpression ReadValueExpression(JsonElement element, string path)
    {
        RequireKind(element, JsonValueKind.Object, path, "object");
        var source = RequiredString(element, "source", path);
        return source switch
        {
            "literal" => ReadLiteral(element, path),
            "field" => ReadFieldValue(element, path),
            "context" => ReadContextValue(element, path),
            "object" => ReadObjectValue(element, path),
            "array" => ReadArrayValue(element, path),
            _ => throw Invalid($"{path}.source '{source}' is not supported.")
        };
    }

    private static RuntimeActionLiteralValue ReadLiteral(JsonElement element, string path)
    {
        EnsureProperties(element, path, "source", "value");
        if (!element.TryGetProperty("value", out var value))
        {
            throw Invalid($"{path}.value is required.");
        }

        return new RuntimeActionLiteralValue(value.Clone());
    }

    private static RuntimeActionFieldValue ReadFieldValue(JsonElement element, string path)
    {
        EnsureProperties(element, path, "source", "name");
        return new RuntimeActionFieldValue(RequiredString(element, "name", path));
    }

    private static RuntimeActionContextValue ReadContextValue(JsonElement element, string path)
    {
        EnsureProperties(element, path, "source", "name");
        var value = RequiredString(element, "name", path);
        var context = value switch
        {
            "nodeId" => RuntimeActionContextValueKind.NodeId,
            "timestampUtc" => RuntimeActionContextValueKind.TimestampUtc,
            "inputPayload" => RuntimeActionContextValueKind.InputPayload,
            _ => throw Invalid($"{path}.name '{value}' is not supported.")
        };

        return new RuntimeActionContextValue(context);
    }

    private static RuntimeActionObjectValue ReadObjectValue(JsonElement element, string path)
    {
        EnsureProperties(element, path, "source", "properties");
        var propertiesElement = RequiredProperty(element, "properties", path, JsonValueKind.Object);
        var properties = new Dictionary<string, RuntimeActionValueExpression>(StringComparer.Ordinal);
        foreach (var property in propertiesElement.EnumerateObject())
        {
            properties.Add(
                property.Name,
                ReadValueExpression(property.Value, $"{path}.properties.{property.Name}"));
        }

        return new RuntimeActionObjectValue(properties);
    }

    private static RuntimeActionArrayValue ReadArrayValue(JsonElement element, string path)
    {
        EnsureProperties(element, path, "source", "items");
        var itemsElement = RequiredProperty(element, "items", path, JsonValueKind.Array);
        return new RuntimeActionArrayValue(itemsElement
            .EnumerateArray()
            .Select((item, index) => ReadValueExpression(item, $"{path}.items[{index}]"))
            .ToArray());
    }

    private static void WriteContract(Utf8JsonWriter writer, RuntimeActionContract contract)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", contract.SchemaVersion);
        writer.WriteString("actionType", contract.ActionType);
        writer.WritePropertyName("fields");
        writer.WriteStartObject();
        foreach (var (fieldName, definition) in contract.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(fieldName);
            WriteFieldDefinition(writer, definition);
        }

        writer.WriteEndObject();
        writer.WritePropertyName("emit");
        WriteEmit(writer, contract.Emit);
        writer.WriteEndObject();
    }

    private static void WriteFieldDefinition(
        Utf8JsonWriter writer,
        RuntimeActionFieldDefinition definition)
    {
        writer.WriteStartObject();
        writer.WriteString("type", FieldType(definition.Type));
        writer.WriteBoolean("required", definition.Required);
        if (definition.AllowedValues is not null)
        {
            writer.WritePropertyName("enum");
            writer.WriteStartArray();
            foreach (var value in definition.AllowedValues.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        if (definition.Minimum is not null)
        {
            WriteCanonicalDecimal(writer, "minimum", definition.Minimum.Value);
        }

        if (definition.Maximum is not null)
        {
            WriteCanonicalDecimal(writer, "maximum", definition.Maximum.Value);
        }

        if (definition.MaxLength is not null)
        {
            writer.WriteNumber("maxLength", definition.MaxLength.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteEmit(Utf8JsonWriter writer, RuntimeActionEmit emit)
    {
        writer.WriteStartObject();
        switch (emit)
        {
            case RuntimeDeviceCommandEmit command:
                writer.WriteString("kind", "deviceCommand");
                writer.WritePropertyName("targetKind");
                WriteValueExpression(writer, command.TargetKind);
                writer.WritePropertyName("targetId");
                WriteValueExpression(writer, command.TargetId);
                writer.WritePropertyName("capability");
                WriteValueExpression(writer, command.Capability);
                writer.WritePropertyName("commandName");
                WriteValueExpression(writer, command.CommandName);
                writer.WritePropertyName("input");
                WriteValueExpression(writer, command.Input);
                writer.WritePropertyName("timeoutMilliseconds");
                WriteValueExpression(writer, command.TimeoutMilliseconds);
                writer.WriteNumber("retryLimit", command.RetryLimit);
                break;
            case RuntimeDelayEmit delay:
                writer.WriteString("kind", "delay");
                writer.WritePropertyName("durationMilliseconds");
                WriteValueExpression(writer, delay.DurationMilliseconds);
                break;
            case RuntimeResultPatchEmit resultPatch:
                writer.WriteString("kind", "resultPatch");
                writer.WritePropertyName("assignments");
                writer.WriteStartArray();
                foreach (var assignment in resultPatch.Assignments)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("key");
                    WriteValueExpression(writer, assignment.Key);
                    writer.WritePropertyName("value");
                    WriteValueExpression(writer, assignment.Value);
                    if (assignment.When is not null)
                    {
                        writer.WritePropertyName("when");
                        writer.WriteStartObject();
                        writer.WriteString("field", assignment.When.FieldName);
                        writer.WriteBoolean("equals", assignment.When.ExpectedValue);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException($"Unsupported emit type {emit.GetType().Name}.");
        }

        writer.WriteEndObject();
    }

    private static void WriteValueExpression(
        Utf8JsonWriter writer,
        RuntimeActionValueExpression expression)
    {
        writer.WriteStartObject();
        switch (expression)
        {
            case RuntimeActionLiteralValue literal:
                writer.WriteString("source", "literal");
                writer.WritePropertyName("value");
                WriteLiteral(writer, literal.Value);
                break;
            case RuntimeActionFieldValue field:
                writer.WriteString("source", "field");
                writer.WriteString("name", field.FieldName);
                break;
            case RuntimeActionContextValue context:
                writer.WriteString("source", "context");
                writer.WriteString("name", ContextValue(context.Context));
                break;
            case RuntimeActionObjectValue objectValue:
                writer.WriteString("source", "object");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                foreach (var (propertyName, value) in objectValue.Properties
                             .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(propertyName);
                    WriteValueExpression(writer, value);
                }

                writer.WriteEndObject();
                break;
            case RuntimeActionArrayValue arrayValue:
                writer.WriteString("source", "array");
                writer.WritePropertyName("items");
                writer.WriteStartArray();
                foreach (var item in arrayValue.Items)
                {
                    WriteValueExpression(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported expression type {expression.GetType().Name}.");
        }

        writer.WriteEndObject();
    }

    private static void WriteLiteral(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                WriteCanonicalDecimalValue(writer, value.GetDecimal());
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported literal value kind {value.ValueKind}.");
        }
    }

    private static void WriteCanonicalDecimal(Utf8JsonWriter writer, string name, decimal value)
    {
        writer.WritePropertyName(name);
        WriteCanonicalDecimalValue(writer, value);
    }

    private static void WriteCanonicalDecimalValue(Utf8JsonWriter writer, decimal value)
    {
        var normalized = value == decimal.Zero
            ? "0"
            : value.ToString("G29", CultureInfo.InvariantCulture);
        writer.WriteRawValue(normalized, skipInputValidation: false);
    }

    private static string? FindDuplicateProperty(JsonElement element, string path, int depth)
    {
        if (depth > 64)
        {
            return path;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    return $"{path}.{property.Name}";
                }

                var duplicate = FindDuplicateProperty(property.Value, $"{path}.{property.Name}", depth + 1);
                if (duplicate is not null)
                {
                    return duplicate;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var duplicate = FindDuplicateProperty(item, $"{path}[{index}]", depth + 1);
                if (duplicate is not null)
                {
                    return duplicate;
                }

                index += 1;
            }
        }

        return null;
    }

    private static void EnsureProperties(JsonElement element, string path, params string[] allowedNames)
    {
        var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid($"{path} contains unknown property '{property.Name}'.");
            }
        }
    }

    private static JsonElement RequiredProperty(
        JsonElement element,
        string name,
        string path,
        JsonValueKind expectedKind)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw Invalid($"{path}.{name} is required.");
        }

        RequireKind(value, expectedKind, $"{path}.{name}", expectedKind.ToString());
        return value;
    }

    private static string RequiredString(JsonElement element, string name, string path)
    {
        return RequiredStringValue(
            RequiredProperty(element, name, path, JsonValueKind.String),
            $"{path}.{name}");
    }

    private static string RequiredStringValue(JsonElement element, string path)
    {
        RequireKind(element, JsonValueKind.String, path, "string");
        return element.GetString() ?? throw Invalid($"{path} cannot be null.");
    }

    private static bool RequiredBoolean(JsonElement element, string name, string path)
    {
        var value = RequiredProperty(element, name, path, JsonValueKind.True, JsonValueKind.False);
        return value.GetBoolean();
    }

    private static int RequiredInt32(JsonElement element, string name, string path)
    {
        var value = RequiredProperty(element, name, path, JsonValueKind.Number);
        return value.TryGetInt32(out var number)
            ? number
            : throw Invalid($"{path}.{name} must be a 32-bit integer.");
    }

    private static long RequiredInt64(JsonElement element, string name, string path)
    {
        var value = RequiredProperty(element, name, path, JsonValueKind.Number);
        return value.TryGetInt64(out var number)
            ? number
            : throw Invalid($"{path}.{name} must be a 64-bit integer.");
    }

    private static int? OptionalInt32(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        RequireKind(value, JsonValueKind.Number, $"{path}.{name}", "number");
        return value.TryGetInt32(out var number)
            ? number
            : throw Invalid($"{path}.{name} must be a 32-bit integer.");
    }

    private static decimal? OptionalDecimal(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        RequireKind(value, JsonValueKind.Number, $"{path}.{name}", "number");
        return value.TryGetDecimal(out var number)
            ? number
            : throw Invalid($"{path}.{name} must be a finite decimal number.");
    }

    private static JsonElement RequiredProperty(
        JsonElement element,
        string name,
        string path,
        params JsonValueKind[] expectedKinds)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw Invalid($"{path}.{name} is required.");
        }

        if (!expectedKinds.Contains(value.ValueKind))
        {
            throw Invalid($"{path}.{name} must be {string.Join(" or ", expectedKinds)}.");
        }

        return value;
    }

    private static void RequireKind(
        JsonElement element,
        JsonValueKind expectedKind,
        string path,
        string expectedDescription)
    {
        if (element.ValueKind != expectedKind)
        {
            throw Invalid($"{path} must be {expectedDescription}.");
        }
    }

    private static RuntimeActionFieldType ReadFieldType(string value, string path)
    {
        return value switch
        {
            "string" => RuntimeActionFieldType.Text,
            "number" => RuntimeActionFieldType.Number,
            "integer" => RuntimeActionFieldType.WholeNumber,
            "boolean" => RuntimeActionFieldType.Boolean,
            "json" => RuntimeActionFieldType.Json,
            "targetReference" => RuntimeActionFieldType.TargetReference,
            _ => throw Invalid($"{path} '{value}' is not supported.")
        };
    }

    private static string FieldType(RuntimeActionFieldType value) => value switch
    {
        RuntimeActionFieldType.Text => "string",
        RuntimeActionFieldType.Number => "number",
        RuntimeActionFieldType.WholeNumber => "integer",
        RuntimeActionFieldType.Boolean => "boolean",
        RuntimeActionFieldType.Json => "json",
        RuntimeActionFieldType.TargetReference => "targetReference",
        _ => throw new InvalidOperationException($"Unsupported field type {value}.")
    };

    private static string ContextValue(RuntimeActionContextValueKind value) => value switch
    {
        RuntimeActionContextValueKind.NodeId => "nodeId",
        RuntimeActionContextValueKind.TimestampUtc => "timestampUtc",
        RuntimeActionContextValueKind.InputPayload => "inputPayload",
        _ => throw new InvalidOperationException($"Unsupported context value {value}.")
    };

    private static ContractFormatException Invalid(string message) => new(message);

    private static Result<T> Failure<T>(string message)
    {
        return Result.Failure<T>(ApplicationError.Validation(
            "Processes.RuntimeActionContractInvalid",
            message));
    }

    private sealed class ContractFormatException(string message) : Exception(message);
}
