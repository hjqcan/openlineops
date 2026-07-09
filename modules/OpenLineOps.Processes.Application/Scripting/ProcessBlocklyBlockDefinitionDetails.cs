namespace OpenLineOps.Processes.Application.Scripting;

public sealed record ProcessBlocklyBlockDefinitionDetails(
    string BlockType,
    string Category,
    string DisplayName,
    string BlocklyJson,
    string PythonCodeTemplate,
    bool IsBuiltIn,
    int Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
