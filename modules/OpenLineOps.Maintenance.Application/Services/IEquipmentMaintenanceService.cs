using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Maintenance.Application.Contracts;

namespace OpenLineOps.Maintenance.Application.Services;

public interface IEquipmentMaintenanceService
{
    ValueTask<Result<EquipmentAssetDetails>> RegisterAsync(
        RegisterEquipmentAssetCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EquipmentAssetDetails>> GetAsync(
        string assetId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EquipmentAssetDetails>> AddPlanAsync(
        AddMaintenancePlanCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EquipmentAssetDetails>> RecordUsageAsync(
        RecordEquipmentUsageCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EquipmentAssetDetails>> EvaluateMaintenanceAsync(
        EvaluateMaintenanceCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EquipmentAssetDetails>> CompleteTaskAsync(
        CompleteMaintenanceTaskCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EquipmentAssetDetails>> RecordCalibrationAsync(
        RecordCalibrationCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<EquipmentAssetDetails>> RecordHealthAsync(
        RecordEquipmentHealthCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<StationProductionStartDecision>> EvaluateProductionStartAsync(
        string stationId,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default);
}
