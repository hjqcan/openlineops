namespace OpenLineOps.Processes.Application.Scripting;

public sealed record ProcessScriptValidationReport(
    IReadOnlyCollection<ProcessScriptValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public static ProcessScriptValidationReport Valid { get; } = new([]);
}
