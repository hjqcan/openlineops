using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Processes.Application.Runtime;

public interface IRuntimeConfigurationSnapshotResolver
{
    ValueTask<Result<RuntimeConfigurationSnapshotDetails>> ResolveAsync(
        string configurationSnapshotId,
        CancellationToken cancellationToken = default);
}
