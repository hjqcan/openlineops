namespace OpenLineOps.Processes.Application.Validation;

public sealed record ProcessGraphValidationIssueDetails(
    string Severity,
    string Code,
    string Message,
    string TargetKind,
    string TargetId);
