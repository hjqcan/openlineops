namespace OpenLineOps.Processes.Application.Scripting;

public sealed record RegisterProcessBlocklyBlockDefinitionRequest(
    string BlockType,
    string Category,
    string DisplayName,
    string BlocklyJson,
    string PythonCodeTemplate);
