namespace OpenLineOps.Recipes.Application.Fencing;

public interface IStationFencingTokenValidator
{
    ValueTask<bool> IsCurrentAsync(
        string stationId,
        long fencingToken,
        CancellationToken cancellationToken = default);
}

public sealed class FailClosedStationFencingTokenValidator
    : IStationFencingTokenValidator
{
    public ValueTask<bool> IsCurrentAsync(
        string stationId,
        long fencingToken,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);
}
