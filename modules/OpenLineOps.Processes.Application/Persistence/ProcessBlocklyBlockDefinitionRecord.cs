namespace OpenLineOps.Processes.Application.Persistence;

public sealed record ProcessBlocklyBlockDefinitionRecord(
    string BlockType,
    string Category,
    string DisplayName,
    string BlocklyJson,
    string PythonCodeTemplate,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
