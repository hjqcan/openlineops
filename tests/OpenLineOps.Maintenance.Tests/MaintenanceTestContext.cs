using OpenLineOps.Maintenance.Application.Contracts;
using OpenLineOps.Maintenance.Application.Services;
using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Domain.Identifiers;

namespace OpenLineOps.Maintenance.Tests;

internal static class MaintenanceTestContext
{
    public static readonly DateTimeOffset Now =
        new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

    public static EquipmentAsset RegisterDomainAsset(
        bool productionCritical = false,
        bool requiresCalibration = false,
        string assetId = "asset-001",
        string stationId = "station-a") =>
        EquipmentAsset.Register(
            new EquipmentAssetId(assetId),
            stationId,
            "Functional tester",
            productionCritical,
            requiresCalibration,
            "register-001",
            "engineer-001",
            Now);

    public static async ValueTask RegisterAsync(
        IEquipmentMaintenanceService service,
        bool productionCritical = false,
        bool requiresCalibration = false,
        string assetId = "asset-001",
        string stationId = "station-a")
    {
        var result = await service.RegisterAsync(
            new RegisterEquipmentAssetCommand(
                assetId,
                stationId,
                "Functional tester",
                productionCritical,
                requiresCalibration,
                "register-001",
                "engineer-001",
                Now));
        Assert.True(result.IsSuccess, result.Error.Message);
    }
}

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "openlineops-maintenance-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string DatabasePath => System.IO.Path.Combine(Path, "maintenance.db");

    public string ConnectionString => $"Data Source={DatabasePath};Pooling=False";

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
