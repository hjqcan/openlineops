namespace OpenLineOps.Processes.Application.Scripting;

public sealed record ProcessScriptValidationIssue(
    string Code,
    string Message,
    int Line,
    int Column);
