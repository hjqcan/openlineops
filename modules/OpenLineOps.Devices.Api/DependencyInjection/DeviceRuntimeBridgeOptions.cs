namespace OpenLineOps.Devices.Api.DependencyInjection;

public sealed class DeviceRuntimeBridgeOptions
{
    public const string RuntimeSectionName = "OpenLineOps:Runtime";

    public const string DevicesRoutingSectionName = "OpenLineOps:Devices:CommandRouting";

    public const string DevicesExecutionSectionName = "OpenLineOps:Devices:CommandExecution";

    public string CommandExecutor { get; set; } = DeviceRuntimeCommandExecutors.Simulator;

    public string RouteResolver { get; set; } = DeviceCommandRouteResolvers.Engineering;
}
