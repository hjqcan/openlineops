namespace OpenLineOps.Processes.Domain.Validation;

public sealed record ProcessGraphValidationIssue(
    ProcessGraphValidationSeverity Severity,
    string Code,
    string Message);
