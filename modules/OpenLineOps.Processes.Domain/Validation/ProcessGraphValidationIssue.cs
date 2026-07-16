namespace OpenLineOps.Processes.Domain.Validation;

public sealed record ProcessGraphValidationIssue
{
    public ProcessGraphValidationIssue(
        ProcessGraphValidationSeverity severity,
        string code,
        string message,
        ProcessGraphValidationTargetKind targetKind,
        string targetId)
    {
        if (!Enum.IsDefined(targetKind))
        {
            throw new ArgumentOutOfRangeException(nameof(targetKind), targetKind, "Validation target kind is not defined.");
        }

        if (string.IsNullOrWhiteSpace(targetId)
            || !string.Equals(targetId, targetId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Validation target id must be a non-empty canonical string.",
                nameof(targetId));
        }

        Severity = severity;
        Code = code;
        Message = message;
        TargetKind = targetKind;
        TargetId = targetId;
    }

    public ProcessGraphValidationSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public ProcessGraphValidationTargetKind TargetKind { get; }

    public string TargetId { get; }
}
