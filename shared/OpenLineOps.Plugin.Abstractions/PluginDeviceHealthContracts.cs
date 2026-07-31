using System.Collections.ObjectModel;

namespace OpenLineOps.Plugin.Abstractions;

public enum PluginDeviceHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3
}

public sealed record PluginDeviceHealthSnapshot
{
    public PluginDeviceHealthSnapshot(
        string sessionId,
        PluginDeviceHealthStatus status,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? lastHeartbeatAtUtc = null,
        string? details = null)
    {
        SessionId = PluginDeviceContractGuard.RequiredCanonical(sessionId, nameof(sessionId));
        Status = PluginDeviceContractGuard.Defined(status, nameof(status));
        ObservedAtUtc = PluginDeviceContractGuard.Utc(observedAtUtc, nameof(observedAtUtc));
        if (lastHeartbeatAtUtc is not null)
        {
            PluginDeviceContractGuard.Utc(lastHeartbeatAtUtc.Value, nameof(lastHeartbeatAtUtc));
            if (lastHeartbeatAtUtc > observedAtUtc)
            {
                throw new ArgumentException(
                    "Last heartbeat cannot be later than the health observation.",
                    nameof(lastHeartbeatAtUtc));
            }
        }

        LastHeartbeatAtUtc = lastHeartbeatAtUtc;
        Details = PluginDeviceContractGuard.OptionalCanonical(details, nameof(details));
    }

    public string SessionId { get; }

    public PluginDeviceHealthStatus Status { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public DateTimeOffset? LastHeartbeatAtUtc { get; }

    public string? Details { get; }
}

public enum PluginDeviceDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed record PluginDeviceDiagnosticEntry
{
    public PluginDeviceDiagnosticEntry(
        string code,
        PluginDeviceDiagnosticSeverity severity,
        string message,
        DateTimeOffset occurredAtUtc,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        Code = PluginDeviceContractGuard.RequiredCanonical(code, nameof(code));
        Severity = PluginDeviceContractGuard.Defined(severity, nameof(severity));
        Message = PluginDeviceContractGuard.RequiredCanonical(message, nameof(message));
        OccurredAtUtc = PluginDeviceContractGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));

        var snapshot = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var attribute in attributes ?? new Dictionary<string, string?>())
        {
            snapshot.Add(
                PluginDeviceContractGuard.RequiredCanonical(attribute.Key, nameof(attributes)),
                attribute.Value);
        }

        Attributes = new ReadOnlyDictionary<string, string?>(snapshot);
    }

    public string Code { get; }

    public PluginDeviceDiagnosticSeverity Severity { get; }

    public string Message { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public IReadOnlyDictionary<string, string?> Attributes { get; }
}

public sealed record PluginDeviceDiagnosticsSnapshot
{
    public PluginDeviceDiagnosticsSnapshot(
        string sessionId,
        DateTimeOffset capturedAtUtc,
        IEnumerable<PluginDeviceDiagnosticEntry>? entries = null)
    {
        SessionId = PluginDeviceContractGuard.RequiredCanonical(sessionId, nameof(sessionId));
        CapturedAtUtc = PluginDeviceContractGuard.Utc(capturedAtUtc, nameof(capturedAtUtc));
        var snapshot = entries?.ToArray() ?? [];
        if (snapshot.Any(static entry => entry is null))
        {
            throw new ArgumentException(
                "Diagnostic entries cannot contain null values.",
                nameof(entries));
        }

        Entries = Array.AsReadOnly(snapshot);
    }

    public string SessionId { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public IReadOnlyList<PluginDeviceDiagnosticEntry> Entries { get; }
}
