using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Commands;

public sealed class InProcessStationSafetyController : IStationSafetyController
{
    public ValueTask<StationSafetyResult> RequestSafeStopAsync(
        StationSafetyRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(StationSafetyResult.Success());
    }
}
