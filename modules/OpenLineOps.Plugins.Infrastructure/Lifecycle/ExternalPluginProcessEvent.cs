namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed record ExternalPluginProcessEvent(
    ExternalPluginProcessEventKind Kind,
    string ProjectId,
    string ApplicationId,
    string PluginId,
    string PackageContentSha256,
    string Message,
    DateTimeOffset OccurredAtUtc,
    string? Detail = null);

public enum ExternalPluginProcessEventKind
{
    Starting = 1,
    Started = 2,
    StartupFailed = 3,
    StartupExited = 4,
    TrustRejected = 5,
    SandboxRejected = 6,
    CommandTimedOut = 7,
    ProcessKilled = 8,
    ProcessExited = 9
}
