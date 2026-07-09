using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Domain.Abstractions.Entities;

namespace OpenLineOps.Devices.Domain.Definitions;

public sealed class DeviceCommandDefinition : Entity<DeviceCommandDefinitionId>
{
    private DeviceCommandDefinition(
        DeviceCommandDefinitionId id,
        DeviceCapabilityId capabilityId,
        string commandName,
        string? inputSchema,
        string? outputSchema,
        TimeSpan timeout,
        int maxRetries)
        : base(id)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Command timeout must be positive.");
        }

        if (maxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "Max retries cannot be negative.");
        }

        CapabilityId = capabilityId;
        CommandName = DeviceIdGuard.NotBlank(commandName, nameof(commandName));
        InputSchema = string.IsNullOrWhiteSpace(inputSchema) ? null : inputSchema.Trim();
        OutputSchema = string.IsNullOrWhiteSpace(outputSchema) ? null : outputSchema.Trim();
        Timeout = timeout;
        MaxRetries = maxRetries;
    }

    public DeviceCapabilityId CapabilityId { get; }

    public string CommandName { get; }

    public string? InputSchema { get; }

    public string? OutputSchema { get; }

    public TimeSpan Timeout { get; }

    public int MaxRetries { get; }

    public static DeviceCommandDefinition Create(
        DeviceCommandDefinitionId id,
        DeviceCapabilityId capabilityId,
        string commandName,
        string? inputSchema,
        string? outputSchema,
        TimeSpan timeout,
        int maxRetries = 0)
    {
        return new DeviceCommandDefinition(
            id,
            capabilityId,
            commandName,
            inputSchema,
            outputSchema,
            timeout,
            maxRetries);
    }
}
