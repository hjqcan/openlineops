using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Tests;

internal static class ProductionRunTestMaterials
{
    public static async ValueTask<ProductionRunAdmission> RegisterAsync(
        IProductionMaterialRepository materials,
        ProductionRun run)
    {
        var unit = ProductionUnit.Register(
            run.ProductionUnitId,
            run.ProductionUnitIdentity.ModelId,
            run.ProductionUnitIdentity.InputKey,
            run.ProductionUnitIdentity.Value,
            run.LotId is null ? null : new ProductionLotId(run.LotId),
            run.ActorId,
            run.CreatedAtUtc.AddTicks(-1));
        if (!await materials.TryAddAsync(unit).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Test Production Unit {unit.Id} could not be registered.");
        }

        var entry = await materials.GetProductionUnitAsync(unit.Id).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Test Production Unit {unit.Id} disappeared.");
        return new ProductionRunAdmission(entry.Aggregate.ToSnapshot(), entry.Revision);
    }
}
