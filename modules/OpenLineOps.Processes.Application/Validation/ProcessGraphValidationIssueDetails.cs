namespace OpenLineOps.Processes.Application.Validation;

public sealed record ProcessGraphValidationIssueDetails(
    string Severity,
    string Code,
    string Message);
