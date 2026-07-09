namespace OpenLineOps.Processes.Domain.Validation;

public sealed class ProcessGraphValidationReport
{
    public ProcessGraphValidationReport(IEnumerable<ProcessGraphValidationIssue> issues)
    {
        Issues = issues.ToList().AsReadOnly();
    }

    public IReadOnlyCollection<ProcessGraphValidationIssue> Issues { get; }

    public bool IsValid => Issues.All(issue => issue.Severity != ProcessGraphValidationSeverity.Error);

    public static ProcessGraphValidationReport Valid()
    {
        return new ProcessGraphValidationReport([]);
    }
}
