namespace OpenLineOps.Devices.Api.DependencyInjection;

public sealed class DeviceCommandExecutionOptions
{
    public string Provider { get; set; } = DeviceCommandExecutorProviders.Fake;
}
