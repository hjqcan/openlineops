using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Plugins.Application.Discovery;
using OpenLineOps.Plugins.Application.Validation;

namespace OpenLineOps.Plugins.Tests;

public sealed class PluginProcessCommandInventoryTests
{
    [Fact]
    public async Task ListProcessCommandsAsyncReturnsCommandsFromCompatibleManifestsOnly()
    {
        var inventory = new PluginProcessCommandInventory(
            new InMemoryPluginPackageCatalog(
                Package(CreateManifest(
                    "plugin.vision",
                    ["process.vision"],
                    [
                        Command("process.vision:inspect", "process.vision", "Inspect"),
                        Command("process.vision:classify", "process.vision", "Classify")
                    ])),
                Package(CreateManifest(
                    "plugin.incompatible",
                    ["process.robot"],
                    [Command("process.robot:move", "process.robot", "Move")],
                    contractVersion: "2.0.0"))),
            new PluginManifestValidator());

        var commands = await inventory.ListProcessCommandsAsync();

        Assert.Equal(2, commands.Count);
        Assert.Contains(commands, command =>
            command.PluginId == "plugin.vision"
            && command.CommandDefinitionId == "process.vision:inspect"
            && command.CommandName == "Inspect");
        Assert.DoesNotContain(commands, command => command.Capability == "process.robot");
    }

    [Fact]
    public async Task FindProcessCommandAsyncMatchesCapabilityAndCommandName()
    {
        var inventory = new PluginProcessCommandInventory(
            new InMemoryPluginPackageCatalog(Package(CreateManifest(
                "plugin.vision",
                ["process.vision"],
                [Command("process.vision:inspect", "process.vision", "Inspect")]))),
            new PluginManifestValidator());

        var command = await inventory.FindProcessCommandAsync("process.vision", "inspect");

        Assert.NotNull(command);
        Assert.Equal("process.vision:inspect", command.CommandDefinitionId);
        Assert.Null(await inventory.FindProcessCommandAsync("process.vision", "Classify"));
        Assert.Null(await inventory.FindProcessCommandAsync("process.robot", "Inspect"));
        Assert.Null(await inventory.FindProcessCommandAsync(" ", "Inspect"));
    }

    private static PluginPackageDescriptor Package(PluginManifest manifest)
    {
        return new PluginPackageDescriptor(manifest, "plugins/test", "plugins/test/openlineops-plugin.json");
    }

    private static PluginManifest CreateManifest(
        string id,
        IReadOnlyCollection<string> capabilities,
        IReadOnlyCollection<PluginProcessCommandDefinition> commands,
        string contractVersion = "1.0.0")
    {
        return new PluginManifest(
            id,
            id,
            "1.0.0",
            PluginKind.ProcessNode,
            $"{id}.dll",
            $"{id}.Plugin",
            capabilities,
            contractVersion,
            "1.0.0",
            ProcessCommands: commands);
    }

    private static PluginProcessCommandDefinition Command(
        string id,
        string capability,
        string commandName)
    {
        return new PluginProcessCommandDefinition(id, capability, commandName);
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
}
