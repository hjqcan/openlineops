namespace OpenLineOps.Plugin.Abstractions;

public enum PluginDeviceCommandIdempotencyClass
{
    Idempotent = 0,
    Conditional = 1,
    NonIdempotent = 2
}

public enum PluginDeviceCommandSafetyClass
{
    Informational = 0,
    Normal = 1,
    Motion = 2,
    SafetyCritical = 3
}

public sealed record PluginDeviceCommandEnvelope
{
    public PluginDeviceCommandEnvelope(
        string commandId,
        long fencingToken,
        DateTimeOffset deadlineUtc,
        PluginDeviceCommandIdempotencyClass idempotencyClass,
        PluginDeviceCommandSafetyClass safetyClass)
    {
        CommandId = PluginDeviceContractGuard.RequiredCanonical(commandId, nameof(commandId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);

        FencingToken = fencingToken;
        DeadlineUtc = PluginDeviceContractGuard.Utc(deadlineUtc, nameof(deadlineUtc));
        IdempotencyClass = PluginDeviceContractGuard.Defined(
            idempotencyClass,
            nameof(idempotencyClass));
        SafetyClass = PluginDeviceContractGuard.Defined(safetyClass, nameof(safetyClass));
    }

    public string CommandId { get; }

    public long FencingToken { get; }

    public DateTimeOffset DeadlineUtc { get; }

    public PluginDeviceCommandIdempotencyClass IdempotencyClass { get; }

    public PluginDeviceCommandSafetyClass SafetyClass { get; }
}
