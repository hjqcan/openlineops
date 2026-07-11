using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionMaterialDomainTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProductionUnitHoldReleaseRestoresPreviousDisposition()
    {
        var unit = CreateUnit();
        Assert.True(unit.MarkNonconforming("visual defect", BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(unit.Hold("awaiting review", BaseTimeUtc.AddSeconds(2)).Succeeded);

        Assert.Equal(ProductionUnitDisposition.Held, unit.Disposition);
        Assert.Equal(ProductionUnitDisposition.Nonconforming, unit.DispositionBeforeHold);
        Assert.Equal("awaiting review", unit.DispositionReason);

        Assert.True(unit.Release(BaseTimeUtc.AddSeconds(3)).Succeeded);
        Assert.Equal(ProductionUnitDisposition.Nonconforming, unit.Disposition);
        Assert.Null(unit.DispositionBeforeHold);
        Assert.Null(unit.DispositionReason);
    }

    [Fact]
    public void HeldProductionUnitCanBePhysicallyTransferredToQuarantine()
    {
        var unit = CreateUnit();
        var station = MaterialLocation.AtStation("line-a", "station-a");
        var quarantine = MaterialLocation.AtStation("line-a", "station-quarantine");
        Assert.True(unit.Arrive(station, BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(unit.Hold("quality review", BaseTimeUtc.AddSeconds(2)).Succeeded);

        var result = unit.Transfer(station, quarantine, BaseTimeUtc.AddSeconds(3));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(quarantine, unit.Location);
        Assert.Equal(ProductionUnitDisposition.Held, unit.Disposition);
    }

    [Fact]
    public void SlotRequiresReservationLoadStartCompleteAndUnloadOrder()
    {
        var address = new SlotAddress("line-a", "station-test", "slot-01");
        var material = MaterialReference.ForProductionUnit(ProductionUnitId.New());
        var slot = SlotOccupancy.Register(address, BaseTimeUtc);

        Assert.False(slot.Load(material, BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.True(slot.Reserve(material, BaseTimeUtc.AddSeconds(1)).Succeeded);
        Assert.Equal(SlotOccupancyStatus.Reserved, slot.Status);
        Assert.Equal(material, slot.Material);
        Assert.True(slot.Load(material, BaseTimeUtc.AddSeconds(2)).Succeeded);
        Assert.Equal(SlotOccupancyStatus.Occupied, slot.Status);
        Assert.True(slot.Start(material, BaseTimeUtc.AddSeconds(3)).Succeeded);
        Assert.Equal(SlotOccupancyStatus.Running, slot.Status);
        Assert.False(slot.Unload(material, BaseTimeUtc.AddSeconds(4)).Succeeded);
        Assert.True(slot.Complete(material, BaseTimeUtc.AddSeconds(4)).Succeeded);
        Assert.True(slot.Unload(material, BaseTimeUtc.AddSeconds(5)).Succeeded);
        Assert.Equal(SlotOccupancyStatus.Available, slot.Status);
        Assert.Null(slot.Material);
    }

    [Theory]
    [InlineData(SlotOccupancyStatus.Reserved)]
    [InlineData(SlotOccupancyStatus.Occupied)]
    [InlineData(SlotOccupancyStatus.Running)]
    public void OccupyingSlotSnapshotRequiresMaterialBinding(SlotOccupancyStatus status)
    {
        var snapshot = new SlotOccupancySnapshot(
            new SlotAddress("line-a", "station-a", "slot-a"),
            status,
            null,
            BaseTimeUtc,
            BaseTimeUtc);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SlotOccupancy.Restore(snapshot));

        Assert.Contains("material binding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeldProductionUnitSnapshotRequiresPreviousDisposition()
    {
        var source = CreateUnit().ToSnapshot();
        var invalid = source with
        {
            Disposition = ProductionUnitDisposition.Held,
            DispositionBeforeHold = null,
            DispositionReason = "review"
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ProductionUnit.Restore(invalid));

        Assert.Contains("hold state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CarrierCannotBeNestedInsideAnotherCarrier()
    {
        var carrier = Carrier.Register(
            new CarrierId("carrier-a"),
            "tray-24",
            24,
            "operator-a",
            BaseTimeUtc);
        var station = MaterialLocation.AtStation("line-a", "station-a");
        Assert.True(carrier.Arrive(station, BaseTimeUtc.AddSeconds(1)).Succeeded);

        var result = carrier.Transfer(
            station,
            MaterialLocation.OnCarrier(new CarrierId("carrier-b"), "position-01"),
            BaseTimeUtc.AddSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Equal("Runtime.CarrierNestingRejected", result.Code);
    }

    [Fact]
    public void GenealogyRejectsSelfReference()
    {
        var unitId = ProductionUnitId.New();

        Assert.Throws<ArgumentException>(() => new MaterialGenealogyLink(
            MaterialGenealogyLinkId.New(),
            unitId,
            unitId,
            "ComponentOf",
            "operation-assembly",
            "operator-a",
            BaseTimeUtc));
    }

    [Fact]
    public void MaterialLocationsEnforceDiscriminatedIdentity()
    {
        var carrierLocation = MaterialLocation.OnCarrier(
            new CarrierId("carrier-a"),
            "position-01");

        Assert.Equal(MaterialLocationKind.CarrierPosition, carrierLocation.Kind);
        Assert.Null(carrierLocation.LineId);
        Assert.Null(carrierLocation.SlotAddress);
        Assert.Equal("carrier-a", carrierLocation.CarrierId?.Value);
    }

    private static ProductionUnit CreateUnit()
    {
        return ProductionUnit.Register(
            ProductionUnitId.New(),
            "product-model-a",
            "serial-number",
            Guid.NewGuid().ToString("N"),
            null,
            "operator-a",
            BaseTimeUtc);
    }
}
