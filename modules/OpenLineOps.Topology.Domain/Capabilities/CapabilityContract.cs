using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Topology.Domain.Identifiers;

namespace OpenLineOps.Topology.Domain.Capabilities;

public sealed class CapabilityContract : Entity<CapabilityContractId>
{
    private CapabilityContract(
        CapabilityContractId id,
        string commandName,
        Version version,
        string? inputSchema,
        string? outputSchema,
        TimeSpan timeout,
        SafetyClass safetyClass)
        : base(id)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Capability timeout must be positive.");
        }

        CommandName = TopologyIdGuard.NotBlank(commandName, nameof(commandName));
        Version = version;
        InputSchema = string.IsNullOrWhiteSpace(inputSchema) ? null : inputSchema.Trim();
        OutputSchema = string.IsNullOrWhiteSpace(outputSchema) ? null : outputSchema.Trim();
        Timeout = timeout;
        SafetyClass = safetyClass;
    }

    public string CommandName { get; }

    public Version Version { get; }

    public string? InputSchema { get; }

    public string? OutputSchema { get; }

    public TimeSpan Timeout { get; }

    public SafetyClass SafetyClass { get; }

    public static CapabilityContract Create(
        CapabilityContractId id,
        string commandName,
        Version version,
        string? inputSchema,
        string? outputSchema,
        TimeSpan timeout,
        SafetyClass safetyClass = SafetyClass.Normal)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new CapabilityContract(
            id,
            commandName,
            version,
            inputSchema,
            outputSchema,
            timeout,
            safetyClass);
    }
}
