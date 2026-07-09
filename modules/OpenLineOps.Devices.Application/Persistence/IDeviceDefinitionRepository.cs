using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Application.Persistence;

public interface IDeviceDefinitionRepository
{
    ValueTask SaveAsync(
        DeviceDefinition definition,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceDefinition?> GetByIdAsync(
        DeviceDefinitionId definitionId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<DeviceDefinition>> ListAsync(
        CancellationToken cancellationToken = default);
}
