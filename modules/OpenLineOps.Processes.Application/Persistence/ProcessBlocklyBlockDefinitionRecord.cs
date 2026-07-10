namespace OpenLineOps.Processes.Application.Persistence;

public sealed record ProcessBlocklyBlockDefinitionRecord(
    string BlockType,
    string Category,
    string DisplayName,
    string BlocklyJson,
    string ExecutionMode,
    string RuntimeActionContractSchemaVersion,
    string RuntimeActionContractJson,
    string RuntimeActionContractSha256,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
