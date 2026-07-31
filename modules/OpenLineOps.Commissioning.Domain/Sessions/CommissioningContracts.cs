namespace OpenLineOps.Commissioning.Domain.Sessions;

public enum CommissioningSessionStatus
{
    Active = 0,
    RecoveryRequired = 1,
    Completed = 2,
    Aborted = 3,
    Expired = 4
}

public enum CommissioningCapability
{
    DeviceDiagnostics = 0,
    SignalMonitoring = 1,
    ManualCommand = 2,
    FlowStep = 3,
    Breakpoint = 4
}

public enum CommissioningActionSafetyClass
{
    Diagnostic = 0,
    Normal = 1,
    Motion = 2,
    SafetyCritical = 3
}

public enum CommissioningActionIdempotencyClass
{
    Idempotent = 0,
    Conditional = 1,
    NonIdempotent = 2
}

public enum CommissioningRecoveryDisposition
{
    Replay = 0,
    Skip = 1,
    Abort = 2
}

public enum CommissioningAuditKind
{
    SessionStarted = 0,
    LeaseRenewed = 1,
    DiagnosticAccessed = 2,
    SignalMonitoringStarted = 3,
    ManualCommandAuthorized = 4,
    FlowStepAuthorized = 5,
    BreakpointSet = 6,
    AutomaticReplayAuthorized = 7,
    RecoveryRequired = 8,
    RecoveryResolved = 9,
    SessionCompleted = 10,
    SessionAborted = 11,
    SessionExpired = 12
}

public sealed record CommissioningAuditEntry(
    long Sequence,
    CommissioningAuditKind Kind,
    string ActorId,
    DateTimeOffset OccurredAtUtc,
    string? SubjectId,
    string? Reason,
    long FencingToken);

public sealed record CommissioningSessionSnapshot(
    string SessionId,
    string StationId,
    string RequestedBy,
    string AuthorizedRole,
    string LeaseId,
    long FencingToken,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    CommissioningSessionStatus Status,
    string? RecoveryReason,
    IReadOnlyCollection<CommissioningCapability> Capabilities,
    IReadOnlyCollection<CommissioningAuditEntry> AuditTrail);

public sealed record CommissioningOperationResult(
    bool Succeeded,
    string Code,
    string Message)
{
    public static CommissioningOperationResult Accepted(string message) =>
        new(true, "Commissioning.Accepted", message);

    public static CommissioningOperationResult Rejected(string code, string message) =>
        new(false, CommissioningGuard.Canonical(code, nameof(code)), message);
}
