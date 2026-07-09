using OpenLineOps.Devices.Domain.Definitions;
using OpenLineOps.Devices.Domain.Identifiers;
using OpenLineOps.Devices.Domain.Operations;

namespace OpenLineOps.Devices.Tests;

public sealed class DeviceDefinitionTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AddCommandRequiresDeclaredCapability()
    {
        var definition = CreateDefinition();
        var command = CreateInspectCommand();

        var result = definition.AddCommand(command);

        Assert.False(result.Succeeded);
        Assert.Equal("Devices.CommandCapabilityMissing", result.Code);
        Assert.Empty(definition.Commands);
    }

    [Fact]
    public void DefinitionAcceptsCapabilitiesAndCommands()
    {
        var definition = CreateDefinition();
        var capability = DeviceCapability.Create(CapabilityId("vision-camera"), "Vision Camera");
        var command = CreateInspectCommand();

        AssertAccepted(definition.AddCapability(capability));
        AssertAccepted(definition.AddCommand(command));

        Assert.Equal("camera-plugin", definition.PluginId);
        Assert.Single(definition.Capabilities);
        var persistedCommand = Assert.Single(definition.Commands);
        Assert.Equal(command.Id, persistedCommand.Id);
        Assert.Equal(TimeSpan.FromSeconds(30), persistedCommand.Timeout);
        Assert.Equal(1, persistedCommand.MaxRetries);
    }

    [Fact]
    public void DuplicateCapabilityAndCommandNamesAreRejected()
    {
        var definition = CreateDefinition();
        AssertAccepted(definition.AddCapability(DeviceCapability.Create(CapabilityId("vision-camera"), "Vision Camera")));
        AssertAccepted(definition.AddCommand(CreateInspectCommand()));

        var duplicateCapability = definition.AddCapability(DeviceCapability.Create(
            CapabilityId("vision-camera"),
            "Duplicate"));
        var duplicateCommandName = definition.AddCommand(DeviceCommandDefinition.Create(
            CommandId("inspect-secondary"),
            CapabilityId("vision-camera"),
            "Inspect",
            inputSchema: null,
            outputSchema: null,
            timeout: TimeSpan.FromSeconds(5)));

        Assert.False(duplicateCapability.Succeeded);
        Assert.Equal("Devices.CapabilityAlreadyExists", duplicateCapability.Code);
        Assert.False(duplicateCommandName.Succeeded);
        Assert.Equal("Devices.CommandNameAlreadyExists", duplicateCommandName.Code);
        Assert.Single(definition.Commands);
    }

    [Fact]
    public void CommandDefinitionRequiresPositiveTimeoutAndNonNegativeRetries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceCommandDefinition.Create(
            CommandId("bad-timeout"),
            CapabilityId("vision-camera"),
            "Inspect",
            inputSchema: null,
            outputSchema: null,
            timeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceCommandDefinition.Create(
            CommandId("bad-retry"),
            CapabilityId("vision-camera"),
            "Inspect",
            inputSchema: null,
            outputSchema: null,
            timeout: TimeSpan.FromSeconds(1),
            maxRetries: -1));
    }

    private static DeviceDefinition CreateDefinition()
    {
        return DeviceDefinition.Create(
            new DeviceDefinitionId("camera-vision"),
            "Vision Camera",
            "camera-plugin",
            CreatedAtUtc);
    }

    private static DeviceCommandDefinition CreateInspectCommand()
    {
        return DeviceCommandDefinition.Create(
            CommandId("inspect"),
            CapabilityId("vision-camera"),
            "Inspect",
            inputSchema: "{\"type\":\"object\"}",
            outputSchema: "{\"type\":\"object\"}",
            timeout: TimeSpan.FromSeconds(30),
            maxRetries: 1);
    }

    private static void AssertAccepted(DeviceOperationResult result)
    {
        Assert.True(result.Succeeded, result.Message);
    }

    private static DeviceCapabilityId CapabilityId(string value)
    {
        return new DeviceCapabilityId(value);
    }

    private static DeviceCommandDefinitionId CommandId(string value)
    {
        return new DeviceCommandDefinitionId(value);
    }
}
