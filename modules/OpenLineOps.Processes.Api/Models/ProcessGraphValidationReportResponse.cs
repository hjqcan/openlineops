namespace OpenLineOps.Processes.Api.Models;

public sealed record ProcessGraphValidationReportResponse(
    bool IsValid,
    IReadOnlyCollection<ProcessGraphValidationIssueResponse> Issues);
