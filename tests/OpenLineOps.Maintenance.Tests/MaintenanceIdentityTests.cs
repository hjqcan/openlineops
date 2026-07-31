using System.Reflection;
using OpenLineOps.Maintenance.Api.Controllers;
using OpenLineOps.Maintenance.Application.Services;
using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Infrastructure.Persistence;

namespace OpenLineOps.Maintenance.Tests;

public sealed class MaintenanceIdentityTests
{
    [Fact]
    public void MaintenanceAssembliesUseOnlyTheProductIdentity()
    {
        var disallowedIdentity = string.Concat("Smart", "Matri", "X");
        var assemblies = new[]
        {
            typeof(MaintenanceEngineeringController).Assembly,
            typeof(EquipmentAsset).Assembly,
            typeof(EquipmentMaintenanceService).Assembly,
            typeof(SqliteEquipmentAssetRepository).Assembly
        };

        foreach (var assembly in assemblies)
        {
            Assert.Equal(
                "OpenLineOps",
                assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
            Assert.DoesNotContain(
                disallowedIdentity,
                assembly.FullName ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
