namespace OpenLineOps.Processes.Application.Validation;

public sealed record ProcessGraphValidationReportDetails(
    bool IsValid,
    IReadOnlyCollection<ProcessGraphValidationIssueDetails> Issues);
