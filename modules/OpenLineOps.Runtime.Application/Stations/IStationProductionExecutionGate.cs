namespace OpenLineOps.Runtime.Application.Stations;

public interface IStationProductionExecutionGate
{
    ValueTask<StationProductionExecutionGateResult> EvaluateAsync(
        string stationSystemId,
        CancellationToken cancellationToken = default);
}

public sealed record StationProductionExecutionGateResult(
    bool Managed,
    bool Allowed,
    string Reason,
    string? Evidence);
