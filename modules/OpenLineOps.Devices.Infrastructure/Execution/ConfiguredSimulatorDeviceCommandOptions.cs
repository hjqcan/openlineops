using OpenLineOps.Devices.Application.Execution;

namespace OpenLineOps.Devices.Infrastructure.Execution;

public sealed class ConfiguredSimulatorDeviceCommandOptions
{
    public const string SectionName = "OpenLineOps:Devices:CommandExecution:ConfiguredSimulator";

    public string DefaultOutcome { get; set; } = nameof(DeviceCommandExecutionOutcome.Rejected);

    public string? DefaultResultPayload { get; set; }

    public string? DefaultFailureReason { get; set; }

    public int DefaultDelayMilliseconds { get; set; }

    public List<ConfiguredSimulatorDeviceCommandRule> Commands { get; } = [];
}

public sealed class ConfiguredSimulatorDeviceCommandRule
{
    public string? CommandDefinitionId { get; set; }

    public string? CapabilityId { get; set; }

    public string? CommandName { get; set; }

    public string Outcome { get; set; } = nameof(DeviceCommandExecutionOutcome.Completed);

    public string? ResultPayload { get; set; }

    public string? FailureReason { get; set; }

    public int DelayMilliseconds { get; set; }
}
