using OpenLineOps.Devices.Application.Execution;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Infrastructure.Execution;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Infrastructure.Persistence;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Capabilities;
using OpenLineOps.Plugins.Application.Commands;
using EngineeringConfigurationSnapshotId = OpenLineOps.Engineering.Domain.Identifiers.ConfigurationSnapshotId;
using EngineeringDeviceBindingId = OpenLineOps.Engineering.Domain.Identifiers.DeviceBindingId;
using EngineeringDeviceCapabilityId = OpenLineOps.Engineering.Domain.Identifiers.DeviceCapabilityId;
using EngineeringProcessDefinitionId = OpenLineOps.Engineering.Domain.Identifiers.ProcessDefinitionId;
using EngineeringProcessVersionId = OpenLineOps.Engineering.Domain.Identifiers.ProcessVersionId;
using EngineeringProjectId = OpenLineOps.Engineering.Domain.Identifiers.EngineeringProjectId;
using EngineeringRecipeId = OpenLineOps.Engineering.Domain.Identifiers.RecipeId;
using EngineeringRecipeVersionId = OpenLineOps.Engineering.Domain.Identifiers.RecipeVersionId;
using EngineeringStationProfileId = OpenLineOps.Engineering.Domain.Identifiers.StationProfileId;
using EngineeringWorkspaceId = OpenLineOps.Engineering.Domain.Identifiers.WorkspaceId;

namespace OpenLineOps.Devices.Tests;

public sealed class EngineeringConfigurationDeviceCommandRouteResolverTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 6, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = CreatedAtUtc.AddMinutes(10);

    [Fact]
    public async Task ResolveAsyncMapsPublishedSnapshotBindingToDeviceRoute()
    {
        var repository = new InMemoryEngineeringProjectRepository();
        await repository.SaveAsync(CreatePublishedProject());
        var resolver = new EngineeringConfigurationDeviceCommandRouteResolver(repository);

        var route = await resolver.ResolveAsync(CreateRouteRequest("device.scanner", "Scan"));

        Assert.NotNull(route);
        Assert.Equal("scanner-01", route.DeviceInstanceId.Value);
        Assert.Equal("device.scanner", route.CapabilityId.Value);
        Assert.Equal("device.scanner:Scan", route.CommandDefinitionId.Value);
    }

    [Fact]
    public async Task ResolveAsyncReturnsNullWhenSnapshotStationDoesNotMatchRuntimeStation()
    {
        var repository = new InMemoryEngineeringProjectRepository();
        await repository.SaveAsync(CreatePublishedProject());
        var resolver = new EngineeringConfigurationDeviceCommandRouteResolver(repository);

        var route = await resolver.ResolveAsync(new DeviceCommandRouteRequest(
            "session-001",
            "step-001",
            "command-001",
            "node-scan",
            "station-other",
            "snapshot-001",
            new DeviceCapabilityId("device.scanner"),
            "Scan"));

        Assert.Null(route);
    }

    [Fact]
    public async Task ResolveAsyncReturnsNullWhenCapabilityIsNotBoundInSnapshot()
    {
        var repository = new InMemoryEngineeringProjectRepository();
        await repository.SaveAsync(CreatePublishedProject());
        var resolver = new EngineeringConfigurationDeviceCommandRouteResolver(repository);

        var route = await resolver.ResolveAsync(CreateRouteRequest("device.multimeter", "MeasureVoltage"));

        Assert.Null(route);
    }

    [Fact]
    public async Task ResolveAsyncRequiresPluginCapabilityWhenInventoryIsProvided()
    {
        var repository = new InMemoryEngineeringProjectRepository();
        await repository.SaveAsync(CreatePublishedProject());
        var resolver = new EngineeringConfigurationDeviceCommandRouteResolver(
            repository,
            new InMemoryPluginCapabilityInventory(
                new PluginCapabilityDescriptor(
                    "plugin.multimeter",
                    "Multimeter",
                    PluginKind.DeviceDriver,
                    "device.multimeter")));

        var route = await resolver.ResolveAsync(CreateRouteRequest("device.scanner", "Scan"));

        Assert.Null(route);
    }

    [Fact]
    public async Task ResolveAsyncAcceptsSnapshotBindingWhenPluginCapabilityIsDeclared()
    {
        var repository = new InMemoryEngineeringProjectRepository();
        await repository.SaveAsync(CreatePublishedProject());
        var resolver = new EngineeringConfigurationDeviceCommandRouteResolver(
            repository,
            new InMemoryPluginCapabilityInventory(
                new PluginCapabilityDescriptor(
                    "plugin.scanner",
                    "Scanner",
                    PluginKind.DeviceDriver,
                    "device.scanner")));

        var route = await resolver.ResolveAsync(CreateRouteRequest("device.scanner", "Scan"));

        Assert.NotNull(route);
        Assert.Equal("scanner-01", route.DeviceInstanceId.Value);
    }

    [Fact]
    public async Task ResolveAsyncRequiresPluginDeviceCommandWhenCommandInventoryIsProvided()
    {
        var repository = new InMemoryEngineeringProjectRepository();
        await repository.SaveAsync(CreatePublishedProject());
        var resolver = new EngineeringConfigurationDeviceCommandRouteResolver(
            repository,
            commandInventory: new InMemoryPluginDeviceCommandInventory(
                new PluginDeviceCommandDescriptor(
                    "plugin.scanner",
                    "Scanner",
                    PluginKind.DeviceDriver,
                    "device.scanner:calibrate",
                    "device.scanner",
                    "Calibrate",
                    null,
                    null,
                    30000,
                    0)));

        var route = await resolver.ResolveAsync(CreateRouteRequest("device.scanner", "Scan"));

        Assert.Null(route);
    }

    [Fact]
    public async Task ResolveAsyncUsesPluginDeviceCommandDefinitionWhenDeclared()
    {
        var repository = new InMemoryEngineeringProjectRepository();
        await repository.SaveAsync(CreatePublishedProject());
        var resolver = new EngineeringConfigurationDeviceCommandRouteResolver(
            repository,
            commandInventory: new InMemoryPluginDeviceCommandInventory(
                new PluginDeviceCommandDescriptor(
                    "plugin.scanner",
                    "Scanner",
                    PluginKind.DeviceDriver,
                    "plugin.scanner.commands.scan",
                    "device.scanner",
                    "Scan",
                    null,
                    null,
                    30000,
                    0)));

        var route = await resolver.ResolveAsync(CreateRouteRequest("device.scanner", "scan"));

        Assert.NotNull(route);
        Assert.Equal("scanner-01", route.DeviceInstanceId.Value);
        Assert.Equal("device.scanner", route.CapabilityId.Value);
        Assert.Equal("plugin.scanner.commands.scan", route.CommandDefinitionId.Value);
    }

    private static EngineeringProject CreatePublishedProject()
    {
        var project = EngineeringProject.Create(
            new EngineeringProjectId("project-001"),
            new EngineeringWorkspaceId("workspace-001"),
            "Project 001",
            CreatedAtUtc);
        var recipe = Recipe.Create(
            new EngineeringRecipeId("recipe-001"),
            new EngineeringRecipeVersionId("recipe-001@1.0.0"),
            "Recipe 001",
            CreatedAtUtc);
        var stationProfile = StationProfile.Create(
            new EngineeringStationProfileId("station-eol"),
            "EOL Station");

        Assert.True(recipe.Publish(PublishedAtUtc).Succeeded);
        Assert.True(stationProfile.AddDeviceBinding(DeviceBinding.Create(
            new EngineeringDeviceBindingId("scanner-primary"),
            new EngineeringDeviceCapabilityId("device.scanner"),
            "scanner-01")).Succeeded);
        Assert.True(project.PublishSnapshot(
            new EngineeringConfigurationSnapshotId("snapshot-001"),
            new EngineeringProcessDefinitionId("process-001"),
            new EngineeringProcessVersionId("process-001@1.0.0"),
            recipe,
            stationProfile,
            PublishedAtUtc).Succeeded);

        return project;
    }

    private static DeviceCommandRouteRequest CreateRouteRequest(string capabilityId, string commandName)
    {
        return new DeviceCommandRouteRequest(
            "session-001",
            "step-001",
            "command-001",
            "node-scan",
            "station-eol",
            "snapshot-001",
            new DeviceCapabilityId(capabilityId),
            commandName);
    }

    private sealed class InMemoryPluginCapabilityInventory(
        params PluginCapabilityDescriptor[] capabilities) : IPluginCapabilityInventory
    {
        public ValueTask<IReadOnlyCollection<PluginCapabilityDescriptor>> ListCapabilitiesAsync(
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyCollection<PluginCapabilityDescriptor>>(capabilities);
        }
    }

    private sealed class InMemoryPluginDeviceCommandInventory(
        params PluginDeviceCommandDescriptor[] commands) : IPluginDeviceCommandInventory
    {
        public ValueTask<IReadOnlyCollection<PluginDeviceCommandDescriptor>> ListDeviceCommandsAsync(
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyCollection<PluginDeviceCommandDescriptor>>(commands);
        }
    }
}
