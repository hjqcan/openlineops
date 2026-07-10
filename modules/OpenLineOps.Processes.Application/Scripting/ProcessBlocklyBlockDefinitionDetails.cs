namespace OpenLineOps.Processes.Application.Scripting;

public sealed record ProcessBlocklyBlockDefinitionDetails(
    string BlockType,
    string Category,
    string DisplayName,
    string BlocklyJson,
    bool IsBuiltIn,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string ExecutionMode = ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
    string? RuntimeActionContractSchemaVersion = null,
    string? RuntimeActionContractJson = null,
    string? RuntimeActionContractSha256 = null);
