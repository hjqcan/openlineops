namespace OpenLineOps.Processes.Api.Models;

public sealed record ProcessGraphValidationIssueResponse(
    string Severity,
    string Code,
    string Message,
    string TargetKind,
    string TargetId);
