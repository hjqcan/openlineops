using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Devices.Application.Configuration;

public interface IDeviceConfigurationService
{
    Task<Result<DeviceDefinitionDetails>> CreateDefinitionAsync(
        CreateDeviceDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DeviceDefinitionDetails>> GetDefinitionAsync(
        string deviceDefinitionId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<DeviceDefinitionDetails>>> ListDefinitionsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<DeviceInstanceDetails>> RegisterInstanceAsync(
        RegisterDeviceInstanceRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DeviceInstanceDetails>> GetInstanceAsync(
        string deviceInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<DeviceInstanceDetails>>> ListInstancesAsync(
        CancellationToken cancellationToken = default);

    Task<Result<DeviceInstanceDetails>> ConnectInstanceAsync(
        string deviceInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result<DeviceInstanceDetails>> DisconnectInstanceAsync(
        string deviceInstanceId,
        DeviceStatusChangeRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DeviceInstanceDetails>> FaultInstanceAsync(
        string deviceInstanceId,
        DeviceStatusChangeRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DeviceInstanceDetails>> ResetFaultAsync(
        string deviceInstanceId,
        CancellationToken cancellationToken = default);
}
