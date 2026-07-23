using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Capabilities;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Validation;

namespace OpenLineOps.Plugins.Tests;

public sealed class PluginCapabilityInventoryTests
{
    private static readonly ProjectApplicationWorkspaceScope Scope = PluginTestScope.Create();

    [Fact]
    public async Task ListCapabilitiesAsyncReturnsCapabilitiesFromCompatibleManifestsOnly()
    {
        var inventory = new PluginCapabilityInventory(
            new InMemoryPluginPackageCatalog(
                Package(CreateManifest(
                    "plugin.scanner",
                    ["device.scanner", "device.camera"])),
                Package(CreateManifest(
                    "plugin.invalid",
                    [],
                    contractVersion: "1.0.0")),
                Package(CreateManifest(
                    "plugin.incompatible",
                    ["device.robot"],
                    contractVersion: "2.0.0"))),
            new PluginManifestValidator());

        var capabilities = await inventory.ListCapabilitiesAsync(Scope);

        Assert.Equal(2, capabilities.Count);
        Assert.Contains(capabilities, capability =>
            capability.PluginId == "plugin.scanner"
            && capability.Capability == "device.scanner");
        Assert.Contains(capabilities, capability =>
            capability.PluginId == "plugin.scanner"
            && capability.Capability == "device.camera");
        Assert.DoesNotContain(capabilities, capability => capability.Capability == "device.robot");
    }

    [Fact]
    public async Task HasCapabilityAsyncChecksCompatibleCapabilityDeclarations()
    {
        var inventory = new PluginCapabilityInventory(
            new InMemoryPluginPackageCatalog(Package(CreateManifest("plugin.scanner", ["device.scanner"]))),
            new PluginManifestValidator());

        Assert.True(await inventory.HasCapabilityAsync("device.scanner", Scope));
        Assert.False(await inventory.HasCapabilityAsync("Device.Scanner", Scope));
        Assert.False(await inventory.HasCapabilityAsync(" device.scanner ", Scope));
        Assert.False(await inventory.HasCapabilityAsync("device.multimeter", Scope));
        Assert.False(await inventory.HasCapabilityAsync(" ", Scope));
    }

    [Fact]
    public async Task InterfaceDefaultLookupRequiresExactCanonicalCapability()
    {
        IPluginCapabilityInventory inventory = new StaticCapabilityInventory(
            new PluginCapabilityDescriptor(
                "plugin.scanner",
                "Scanner",
                PluginKind.DeviceDriver,
                "device.scanner"));

        Assert.True(await inventory.HasCapabilityAsync("device.scanner", Scope));
        Assert.False(await inventory.HasCapabilityAsync("Device.Scanner", Scope));
        Assert.False(await inventory.HasCapabilityAsync(" device.scanner ", Scope));
    }

    private static PluginPackageDescriptor Package(PluginManifest manifest)
    {
        return new PluginPackageDescriptor(manifest, "plugins/test", "plugins/test/manifest.json");
    }

    private static PluginManifest CreateManifest(
        string id,
        IReadOnlyCollection<string> capabilities,
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
            "1.0.0");
    }

    private sealed class InMemoryPluginPackageCatalog(
        params PluginPackageDescriptor[] packages) : IPluginPackageCatalog
    {
        public ValueTask<IReadOnlyCollection<PluginPackageDescriptor>> DiscoverAsync(
            ProjectApplicationWorkspaceScope scope,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyCollection<PluginPackageDescriptor>>(packages);
        }
    }

    private sealed class StaticCapabilityInventory(
        params PluginCapabilityDescriptor[] capabilities) : IPluginCapabilityInventory
    {
        public ValueTask<IReadOnlyCollection<PluginCapabilityDescriptor>> ListCapabilitiesAsync(
            ProjectApplicationWorkspaceScope scope,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyCollection<PluginCapabilityDescriptor>>(capabilities);
        }
    }
}
