namespace OpenLineOps.Runtime.Application.Scripting;

public sealed record RuntimeScriptCommandPayload(
    string ScriptLanguage,
    string ScriptSourceCode,
    string? ScriptVersion,
    string? InputPayload);
