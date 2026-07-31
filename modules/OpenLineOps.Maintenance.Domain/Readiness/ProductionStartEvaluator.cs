using OpenLineOps.Maintenance.Domain.Assets;

namespace OpenLineOps.Maintenance.Domain.Readiness;

public enum ProductionStartBlockReason
{
    CalibrationMissing = 0,
    CalibrationExpired = 1,
    CalibrationInvalid = 2,
    MaintenanceDue = 3,
    CriticalEquipmentHealth = 4
}

public sealed record ProductionStartBlock(
    string AssetId,
    ProductionStartBlockReason Reason,
    string SubjectId,
    string Detail);

public sealed record ProductionStartDecision(
    bool Allowed,
    IReadOnlyCollection<ProductionStartBlock> Blocks);

public static class ProductionStartEvaluator
{
    public static ProductionStartDecision Evaluate(
        IEnumerable<EquipmentAsset> assets,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(assets);
        MaintenanceGuard.RequireUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));
        var blocks = assets
            .OrderBy(static asset => asset.Id.Value, StringComparer.Ordinal)
            .SelectMany(asset => EvaluateAsset(asset, evaluatedAtUtc))
            .ToArray();
        return new ProductionStartDecision(blocks.Length == 0, blocks);
    }

    private static IEnumerable<ProductionStartBlock> EvaluateAsset(
        EquipmentAsset asset,
        DateTimeOffset evaluatedAtUtc)
    {
        if (asset.RequiresCalibration)
        {
            if (asset.Calibration is null
                || asset.Calibration.Status == CalibrationStatus.Unknown)
            {
                yield return Block(
                    asset,
                    ProductionStartBlockReason.CalibrationMissing,
                    asset.Id.Value,
                    "Required calibration has not been recorded.");
            }
            else if (asset.Calibration.Status == CalibrationStatus.Valid
                     && asset.Calibration.ValidUntilUtc <= evaluatedAtUtc
                     || asset.Calibration.Status == CalibrationStatus.Expired)
            {
                yield return Block(
                    asset,
                    ProductionStartBlockReason.CalibrationExpired,
                    asset.Calibration.Reference,
                    "Required calibration is expired.");
            }
            else if (asset.Calibration.Status is CalibrationStatus.Invalid
                     or CalibrationStatus.NotRequired)
            {
                yield return Block(
                    asset,
                    ProductionStartBlockReason.CalibrationInvalid,
                    asset.Calibration.Reference,
                    "Required calibration is not valid.");
            }
        }

        if (asset.ProductionCritical
            && asset.HealthStatus is EquipmentHealthStatus.Unknown
                or EquipmentHealthStatus.Critical
                or EquipmentHealthStatus.Unavailable)
        {
            yield return Block(
                asset,
                ProductionStartBlockReason.CriticalEquipmentHealth,
                asset.Id.Value,
                $"Production-critical equipment health is {asset.HealthStatus}.");
        }

        foreach (var plan in asset.Plans.Where(static plan => plan.BlocksProduction))
        {
            var openTask = asset.Tasks.FirstOrDefault(
                task => task.PlanId == plan.Id
                    && task.Status == MaintenanceTaskStatus.Due);
            var dueReason = openTask?.DueReason
                ?? plan.GetDueReason(
                    asset.TotalCycles,
                    asset.TotalOperatingHours,
                    evaluatedAtUtc);
            if (dueReason == MaintenanceDueReason.None)
            {
                continue;
            }

            yield return Block(
                asset,
                ProductionStartBlockReason.MaintenanceDue,
                openTask?.Id.Value ?? plan.Id.Value,
                $"Preventive maintenance is due: {dueReason}.");
        }
    }

    private static ProductionStartBlock Block(
        EquipmentAsset asset,
        ProductionStartBlockReason reason,
        string subjectId,
        string detail) =>
        new(asset.Id.Value, reason, subjectId, detail);
}
