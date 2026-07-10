using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Validation;

namespace OpenLineOps.Plugins.Tests;

public sealed class PluginDeviceCommandInventoryTests
{
    [Fact]
    public async Task ListDeviceCommandsAsyncReturnsCommandsFromCompatibleManifestsOnly()
    {
        var inventory = new PluginDeviceCommandInventory(
            new InMemoryPluginPackageCatalog(
                Package(CreateManifest(
                    "plugin.scanner",
                    ["device.scanner"],
                    [
                        Command("device.scanner:scan", "device.scanner", "Scan"),
                        Command("device.scanner:calibrate", "device.scanner", "Calibrate")
                    ])),
                Package(CreateManifest(
                    "plugin.incompatible",
                    ["device.robot"],
                    [Command("device.robot:move", "device.robot", "Move")],
                    contractVersion: "2.0.0"))),
            new PluginManifestValidator());

        var commands = await inventory.ListDeviceCommandsAsync();

        Assert.Equal(2, commands.Count);
        Assert.Contains(commands, command =>
            command.PluginId == "plugin.scanner"
            && command.CommandDefinitionId == "device.scanner:scan"
            && command.CommandName == "Scan");
        Assert.DoesNotContain(commands, command => command.Capability == "device.robot");
    }

    [Fact]
    public async Task FindDeviceCommandAsyncMatchesCapabilityAndCommandName()
    {
        var inventory = new PluginDeviceCommandInventory(
            new InMemoryPluginPackageCatalog(Package(CreateManifest(
                "plugin.scanner",
                ["device.scanner"],
                [Command("device.scanner:scan", "device.scanner", "Scan")]))),
            new PluginManifestValidator());

        var command = await inventory.FindDeviceCommandAsync("device.scanner", "Scan");

        Assert.NotNull(command);
        Assert.Equal("device.scanner:scan", command.CommandDefinitionId);
        Assert.Null(await inventory.FindDeviceCommandAsync("device.scanner", "scan"));
        Assert.Null(await inventory.FindDeviceCommandAsync(" device.scanner ", "Scan"));
        Assert.Null(await inventory.FindDeviceCommandAsync("device.scanner", " Scan "));
        Assert.Null(await inventory.FindDeviceCommandAsync("device.scanner", "Calibrate"));
        Assert.Null(await inventory.FindDeviceCommandAsync("device.camera", "Scan"));
        Assert.Null(await inventory.FindDeviceCommandAsync(" ", "Scan"));
    }

    [Fact]
    public async Task InterfaceDefaultLookupRequiresExactCanonicalCommandIdentity()
    {
        IPluginDeviceCommandInventory inventory = new StaticDeviceCommandInventory(
            new PluginDeviceCommandDescriptor(
                "plugin.scanner",
                "Scanner",
                PluginKind.DeviceDriver,
                "device.scanner:scan",
                "device.scanner",
                "Scan",
                null,
                null,
                30000,
                0));

        Assert.NotNull(await inventory.FindDeviceCommandAsync("device.scanner", "Scan"));
        Assert.Null(await inventory.FindDeviceCommandAsync("Device.Scanner", "Scan"));
        Assert.Null(await inventory.FindDeviceCommandAsync("device.scanner", "scan"));
        Assert.Null(await inventory.FindDeviceCommandAsync(" device.scanner ", "Scan"));
        Assert.Null(await inventory.FindDeviceCommandAsync("device.scanner", " Scan "));
    }

    private static PluginPackageDescriptor Package(PluginManifest manifest)
    {
        return new PluginPackageDescriptor(manifest, "plugins/test", "plugins/test/manifest.json");
    }

    private static PluginManifest CreateManifest(
        string id,
        IReadOnlyCollection<string> capabilities,
        IReadOnlyCollection<PluginDeviceCommandDefinition> commands,
        string contractVersion = "1.0.0")
    {
        return new PluginManifest(
            id,
            id,
            "1.0.0",
            PluginKind.DeviceDriver,
            $"{id}.dll",
            $"{id}.Plugin",
            capabilities,
            contractVersion,
            "1.0.0",
            commands);
    }

    private static PluginDeviceCommandDefinition Command(
        string id,
        string capability,
        string commandName)
    {
        return new PluginDeviceCommandDefinition(id, capability, commandName);
    }

    private sealed class InMemoryPluginPackageCatalog(
        params PluginPackageDescriptor[] packages) : IPluginPackageCatalog
    {
        public ValueTask<IReadOnlyCollection<PluginPackageDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyCollection<PluginPackageDescriptor>>(packages);
        }
    }

    private sealed class StaticDeviceCommandInventory(
        params PluginDeviceCommandDescriptor[] commands) : IPluginDeviceCommandInventory
    {
        public ValueTask<IReadOnlyCollection<PluginDeviceCommandDescriptor>> ListDeviceCommandsAsync(
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyCollection<PluginDeviceCommandDescriptor>>(commands);
        }
    }
}
