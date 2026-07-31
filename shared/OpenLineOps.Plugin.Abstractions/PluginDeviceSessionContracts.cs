namespace OpenLineOps.Plugin.Abstractions;

public sealed record PluginDeviceSessionOpenRequest
{
    public PluginDeviceSessionOpenRequest(
        string deviceInstanceId,
        string? configurationPayload = null)
    {
        DeviceInstanceId = PluginDeviceContractGuard.RequiredCanonical(
            deviceInstanceId,
            nameof(deviceInstanceId));
        ConfigurationPayload = configurationPayload;
    }

    public string DeviceInstanceId { get; }

    public string? ConfigurationPayload { get; }
}

public sealed record PluginDeviceSession
{
    public PluginDeviceSession(
        string sessionId,
        string deviceInstanceId,
        DateTimeOffset openedAtUtc,
        TimeSpan heartbeatInterval)
    {
        SessionId = PluginDeviceContractGuard.RequiredCanonical(sessionId, nameof(sessionId));
        DeviceInstanceId = PluginDeviceContractGuard.RequiredCanonical(
            deviceInstanceId,
            nameof(deviceInstanceId));
        OpenedAtUtc = PluginDeviceContractGuard.Utc(openedAtUtc, nameof(openedAtUtc));
        HeartbeatInterval = PluginDeviceContractGuard.PositiveWholeMilliseconds(
            heartbeatInterval,
            nameof(heartbeatInterval));
    }

    public string SessionId { get; }

    public string DeviceInstanceId { get; }

    public DateTimeOffset OpenedAtUtc { get; }

    public TimeSpan HeartbeatInterval { get; }
}

public sealed record PluginDeviceSessionRequest
{
    public PluginDeviceSessionRequest(string sessionId)
    {
        SessionId = PluginDeviceContractGuard.RequiredCanonical(sessionId, nameof(sessionId));
    }

    public string SessionId { get; }
}

public sealed record PluginDeviceSessionCloseRequest
{
    public PluginDeviceSessionCloseRequest(string sessionId, string? reason = null)
    {
        SessionId = PluginDeviceContractGuard.RequiredCanonical(sessionId, nameof(sessionId));
        Reason = PluginDeviceContractGuard.OptionalCanonical(reason, nameof(reason));
    }

    public string SessionId { get; }

    public string? Reason { get; }
}
