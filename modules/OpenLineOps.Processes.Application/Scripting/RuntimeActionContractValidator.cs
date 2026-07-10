using System.Text.Json;
using System.Text.RegularExpressions;
using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Processes.Application.Scripting;

public static partial class RuntimeActionContractValidator
{
    private const int MaximumFields = 128;
    private const int MaximumCollectionItems = 256;
    private const int MaximumExpressionDepth = 16;
    private const int MaximumIdentifierLength = 128;
    private const int MaximumStringLength = 4096;

    public static Result<RuntimeActionContract> Validate(RuntimeActionContract? contract)
    {
        if (contract is null)
        {
            return Failure("Contract is required.");
        }

        if (!string.Equals(
                contract.SchemaVersion,
                RuntimeActionContractSchemaVersions.V1,
                StringComparison.Ordinal))
        {
            return Failure($"Schema version '{contract.SchemaVersion}' is not supported.");
        }

        if (!IsIdentifier(contract.ActionType, ActionTypeRegex()))
        {
            return Failure("ActionType must be a canonical dotted identifier.");
        }

        if (contract.Fields is null)
        {
            return Failure("Fields object is required.");
        }

        if (contract.Fields.Count > MaximumFields)
        {
            return Failure($"Fields cannot contain more than {MaximumFields} entries.");
        }

        foreach (var (fieldName, definition) in contract.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsIdentifier(fieldName, FieldNameRegex()))
            {
                return Failure($"Field name '{fieldName}' is invalid.");
            }

            var fieldError = ValidateFieldDefinition(fieldName, definition);
            if (fieldError is not null)
            {
                return Failure(fieldError);
            }
        }

        if (contract.Emit is null)
        {
            return Failure("Emit object is required.");
        }

        var emitError = contract.Emit switch
        {
            RuntimeDeviceCommandEmit command => ValidateDeviceCommand(command, contract.Fields),
            RuntimeDelayEmit delay => ValidateDelay(delay, contract.Fields),
            RuntimeResultPatchEmit resultPatch => ValidateResultPatch(resultPatch, contract.Fields),
            _ => $"Emit type '{contract.Emit.GetType().Name}' is not supported."
        };

        return emitError is null
            ? Result.Success(contract)
            : Failure(emitError);
    }

    private static string? ValidateFieldDefinition(
        string fieldName,
        RuntimeActionFieldDefinition? definition)
    {
        if (definition is null || !Enum.IsDefined(definition.Type))
        {
            return $"Field '{fieldName}' has an invalid type.";
        }

        var isNumeric = definition.Type is RuntimeActionFieldType.Number or RuntimeActionFieldType.WholeNumber;
        if (!isNumeric && (definition.Minimum is not null || definition.Maximum is not null))
        {
            return $"Field '{fieldName}' can declare numeric bounds only when its type is number or integer.";
        }

        if (definition.Minimum > definition.Maximum)
        {
            return $"Field '{fieldName}' minimum cannot exceed its maximum.";
        }

        if (definition.MaxLength is <= 0 or > MaximumStringLength)
        {
            return $"Field '{fieldName}' maxLength must be between 1 and {MaximumStringLength}.";
        }

        if (definition.MaxLength is not null
            && definition.Type is not RuntimeActionFieldType.Text
                and not RuntimeActionFieldType.TargetReference)
        {
            return $"Field '{fieldName}' can declare maxLength only when its type is string or targetReference.";
        }

        if (definition.AllowedValues is null)
        {
            return null;
        }

        if (definition.Type is not RuntimeActionFieldType.Text
            and not RuntimeActionFieldType.TargetReference)
        {
            return $"Field '{fieldName}' can declare enum values only when its type is string or targetReference.";
        }

        if (definition.AllowedValues.Count == 0
            || definition.AllowedValues.Count > MaximumCollectionItems)
        {
            return $"Field '{fieldName}' enum must contain between 1 and {MaximumCollectionItems} values.";
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in definition.AllowedValues)
        {
            if (!IsCanonicalString(value, MaximumStringLength))
            {
                return $"Field '{fieldName}' contains an invalid enum value.";
            }

            if (!values.Add(value))
            {
                return $"Field '{fieldName}' contains duplicate enum value '{value}'.";
            }
        }

        return null;
    }

    private static string? ValidateDeviceCommand(
        RuntimeDeviceCommandEmit command,
        IReadOnlyDictionary<string, RuntimeActionFieldDefinition> fields)
    {
        if (!IsIdentifier(command.Capability, CapabilityRegex()))
        {
            return "Device command capability must be a fixed canonical identifier.";
        }

        if (!IsIdentifier(command.CommandName, CommandNameRegex()))
        {
            return "Device command name must be a fixed canonical identifier.";
        }

        if (command.TimeoutMilliseconds <= 0
            || command.TimeoutMilliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
        {
            return "Device command timeoutMilliseconds must be a positive whole number supported by TimeSpan.";
        }

        if (command.RetryLimit != 0)
        {
            return "Runtime Action Contract v1 supports only retryLimit 0.";
        }

        return ValidateExpression(command.Input, fields, "emit.input", depth: 0);
    }

    private static string? ValidateDelay(
        RuntimeDelayEmit delay,
        IReadOnlyDictionary<string, RuntimeActionFieldDefinition> fields)
    {
        var expressionError = ValidateExpression(
            delay.DurationMilliseconds,
            fields,
            "emit.durationMilliseconds",
            depth: 0);
        if (expressionError is not null)
        {
            return expressionError;
        }

        return IsNumericExpression(delay.DurationMilliseconds, fields)
            ? null
            : "Delay durationMilliseconds must resolve from a numeric literal or numeric field.";
    }

    private static string? ValidateResultPatch(
        RuntimeResultPatchEmit resultPatch,
        IReadOnlyDictionary<string, RuntimeActionFieldDefinition> fields)
    {
        if (resultPatch.Assignments is null
            || resultPatch.Assignments.Count == 0
            || resultPatch.Assignments.Count > MaximumCollectionItems)
        {
            return $"Result patch assignments must contain between 1 and {MaximumCollectionItems} entries.";
        }

        for (var index = 0; index < resultPatch.Assignments.Count; index += 1)
        {
            var assignment = resultPatch.Assignments[index];
            if (assignment is null)
            {
                return $"Result patch assignment {index + 1} cannot be null.";
            }

            var keyPath = $"emit.assignments[{index}].key";
            var keyError = ValidateExpression(assignment.Key, fields, keyPath, depth: 0);
            if (keyError is not null)
            {
                return keyError;
            }

            if (!IsStringExpression(assignment.Key, fields))
            {
                return $"{keyPath} must resolve from a string literal or string field.";
            }

            var valueError = ValidateExpression(
                assignment.Value,
                fields,
                $"emit.assignments[{index}].value",
                depth: 0);
            if (valueError is not null)
            {
                return valueError;
            }

            if (assignment.When is null)
            {
                continue;
            }

            if (!IsIdentifier(assignment.When.FieldName, FieldNameRegex())
                || !fields.TryGetValue(assignment.When.FieldName, out var conditionField)
                || conditionField.Type != RuntimeActionFieldType.Boolean)
            {
                return $"Result patch assignment {index + 1} condition must reference a declared boolean field.";
            }
        }

        return null;
    }

    private static string? ValidateExpression(
        RuntimeActionValueExpression? expression,
        IReadOnlyDictionary<string, RuntimeActionFieldDefinition> fields,
        string path,
        int depth)
    {
        if (expression is null)
        {
            return $"{path} cannot be null.";
        }

        if (depth > MaximumExpressionDepth)
        {
            return $"{path} exceeds the maximum expression depth of {MaximumExpressionDepth}.";
        }

        switch (expression)
        {
            case RuntimeActionLiteralValue literal:
                return ValidateLiteral(literal.Value, path);
            case RuntimeActionFieldValue field:
                return IsIdentifier(field.FieldName, FieldNameRegex())
                       && fields.ContainsKey(field.FieldName)
                    ? null
                    : $"{path} references undeclared field '{field.FieldName}'.";
            case RuntimeActionContextValue context:
                return Enum.IsDefined(context.Context)
                    ? null
                    : $"{path} contains an unsupported context value.";
            case RuntimeActionObjectValue objectValue:
                if (objectValue.Properties is null
                    || objectValue.Properties.Count > MaximumCollectionItems)
                {
                    return $"{path} properties are missing or exceed {MaximumCollectionItems} entries.";
                }

                foreach (var (propertyName, value) in objectValue.Properties
                             .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!IsCanonicalString(propertyName, MaximumIdentifierLength))
                    {
                        return $"{path} contains invalid property name '{propertyName}'.";
                    }

                    var childError = ValidateExpression(
                        value,
                        fields,
                        $"{path}.properties.{propertyName}",
                        depth + 1);
                    if (childError is not null)
                    {
                        return childError;
                    }
                }

                return null;
            case RuntimeActionArrayValue arrayValue:
                if (arrayValue.Items is null || arrayValue.Items.Count > MaximumCollectionItems)
                {
                    return $"{path} items are missing or exceed {MaximumCollectionItems} entries.";
                }

                for (var index = 0; index < arrayValue.Items.Count; index += 1)
                {
                    var childError = ValidateExpression(
                        arrayValue.Items[index],
                        fields,
                        $"{path}.items[{index}]",
                        depth + 1);
                    if (childError is not null)
                    {
                        return childError;
                    }
                }

                return null;
            default:
                return $"{path} expression type '{expression.GetType().Name}' is not supported.";
        }
    }

    private static string? ValidateLiteral(JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return null;
            case JsonValueKind.String:
                return value.GetString()?.Length <= MaximumStringLength
                    ? null
                    : $"{path} string literal exceeds {MaximumStringLength} characters.";
            case JsonValueKind.Number:
                return value.TryGetDecimal(out _)
                    ? null
                    : $"{path} numeric literal must be a finite decimal value.";
            default:
                return $"{path} literal must be null, boolean, string, or a finite decimal number.";
        }
    }

    private static bool IsNumericExpression(
        RuntimeActionValueExpression expression,
        IReadOnlyDictionary<string, RuntimeActionFieldDefinition> fields)
    {
        return expression switch
        {
            RuntimeActionLiteralValue literal => literal.Value.ValueKind == JsonValueKind.Number
                                                 && literal.Value.TryGetDecimal(out _),
            RuntimeActionFieldValue field => fields.TryGetValue(field.FieldName, out var definition)
                                             && definition.Type is RuntimeActionFieldType.Number
                                                 or RuntimeActionFieldType.WholeNumber,
            _ => false
        };
    }

    private static bool IsStringExpression(
        RuntimeActionValueExpression expression,
        IReadOnlyDictionary<string, RuntimeActionFieldDefinition> fields)
    {
        return expression switch
        {
            RuntimeActionLiteralValue literal => literal.Value.ValueKind == JsonValueKind.String
                                                 && !string.IsNullOrWhiteSpace(literal.Value.GetString()),
            RuntimeActionFieldValue field => fields.TryGetValue(field.FieldName, out var definition)
                                             && definition.Type is RuntimeActionFieldType.Text
                                                 or RuntimeActionFieldType.TargetReference,
            _ => false
        };
    }

    private static bool IsIdentifier(string? value, Regex pattern)
    {
        return value is not null
            && value.Length <= MaximumIdentifierLength
            && string.Equals(value, value.Trim(), StringComparison.Ordinal)
            && pattern.IsMatch(value);
    }

    private static bool IsCanonicalString(string? value, int maximumLength)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
            && string.Equals(value, value.Trim(), StringComparison.Ordinal)
            && !value.Any(char.IsControl);
    }

    private static Result<RuntimeActionContract> Failure(string message)
    {
        return Result.Failure<RuntimeActionContract>(ApplicationError.Validation(
            "Processes.RuntimeActionContractInvalid",
            message));
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ActionTypeRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNameRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandNameRegex();
}
