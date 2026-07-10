using System.Text.Json;

namespace OpenLineOps.Processes.Application.Scripting;

public static class RuntimeActionContractSchemaVersions
{
    public const string V1 = "openlineops.runtime-action-contract/v1";
}

public static class ProcessBlocklyBlockExecutionModes
{
    public const string DeclarativeActionContract = "DeclarativeActionContract";

    public const string LegacyPythonTemplate = "LegacyPythonTemplate";
}

public sealed record RuntimeActionContract(
    string SchemaVersion,
    string ActionType,
    IReadOnlyDictionary<string, RuntimeActionFieldDefinition> Fields,
    RuntimeActionEmit Emit);

public sealed record RuntimeActionFieldDefinition(
    RuntimeActionFieldType Type,
    bool Required,
    IReadOnlyCollection<string>? AllowedValues = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    int? MaxLength = null);

public abstract record RuntimeActionEmit;

public sealed record RuntimeDeviceCommandEmit(
    string Capability,
    string CommandName,
    RuntimeActionValueExpression Input,
    long TimeoutMilliseconds,
    int RetryLimit = 0) : RuntimeActionEmit;

public sealed record RuntimeDelayEmit(
    RuntimeActionValueExpression DurationMilliseconds) : RuntimeActionEmit;

public sealed record RuntimeResultPatchEmit(
    IReadOnlyList<RuntimeResultPatchAssignment> Assignments) : RuntimeActionEmit;

public sealed record RuntimeResultPatchAssignment(
    RuntimeActionValueExpression Key,
    RuntimeActionValueExpression Value,
    RuntimeActionFieldEqualsCondition? When = null);

public sealed record RuntimeActionFieldEqualsCondition(
    string FieldName,
    bool ExpectedValue);

public abstract record RuntimeActionValueExpression;

public sealed record RuntimeActionLiteralValue(
    JsonElement Value) : RuntimeActionValueExpression;

public sealed record RuntimeActionFieldValue(
    string FieldName) : RuntimeActionValueExpression;

public sealed record RuntimeActionContextValue(
    RuntimeActionContextValueKind Context) : RuntimeActionValueExpression;

public sealed record RuntimeActionObjectValue(
    IReadOnlyDictionary<string, RuntimeActionValueExpression> Properties) : RuntimeActionValueExpression;

public sealed record RuntimeActionArrayValue(
    IReadOnlyList<RuntimeActionValueExpression> Items) : RuntimeActionValueExpression;

public enum RuntimeActionFieldType
{
    Text = 1,
    Number = 2,
    WholeNumber = 3,
    Boolean = 4,
    Json = 5,
    TargetReference = 6
}

public enum RuntimeActionContextValueKind
{
    NodeId = 1,
    TimestampUtc = 2,
    InputPayload = 3
}

public sealed record RuntimeActionContractCanonicalArtifact(
    string SchemaVersion,
    string CanonicalJson,
    string Sha256);
