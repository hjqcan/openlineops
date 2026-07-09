using System.Collections.Concurrent;
using OpenLineOps.Devices.Application.Persistence;
using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Identifiers;

namespace OpenLineOps.Devices.Infrastructure.Persistence;

public sealed class InMemoryDeviceDefinitionRepository : IDeviceDefinitionRepository
{
    private readonly ConcurrentDictionary<string, DeviceDefinition> _definitions = new(StringComparer.Ordinal);

    public ValueTask SaveAsync(
        DeviceDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        _definitions[definition.Id.Value] = definition;

        return ValueTask.CompletedTask;
    }

    public ValueTask<DeviceDefinition?> GetByIdAsync(
        DeviceDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitionId);
        cancellationToken.ThrowIfCancellationRequested();

        _definitions.TryGetValue(definitionId.Value, out var definition);

        return ValueTask.FromResult(definition);
    }

    public ValueTask<IReadOnlyCollection<DeviceDefinition>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyCollection<DeviceDefinition>>(
            _definitions.Values
                .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
                .ToArray());
    }
}
