using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Application.Compatibility;
using OpenLineOps.Plugins.Application.Validation;

namespace OpenLineOps.Plugins.Tests;

public sealed class PluginManifestValidatorTests
{
    [Fact]
    public void ValidateAcceptsCompatibleManifest()
    {
        var validator = new PluginManifestValidator(new PluginCompatibilityOptions("1.2.0", "1.0.0"));

        var report = validator.Validate(CreateManifest());

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void ValidateRejectsIncompleteManifest()
    {
        var validator = new PluginManifestValidator();
        var manifest = new PluginManifest(
            Id: " ",
            Name: "",
            Version: "not-a-version",
            Kind: (PluginKind)999,
            EntryAssembly: "",
            EntryType: "",
            Capabilities: []);

        var report = validator.Validate(manifest);
        var codes = report.Issues.Select(issue => issue.Code).ToArray();

        Assert.False(report.IsValid);
        Assert.Contains("Plugin.ManifestIdRequired", codes);
        Assert.Contains("Plugin.NameRequired", codes);
        Assert.Contains("Plugin.VersionInvalid", codes);
        Assert.Contains("Plugin.KindUnsupported", codes);
        Assert.Contains("Plugin.EntryAssemblyRequired", codes);
        Assert.Contains("Plugin.EntryTypeRequired", codes);
        Assert.Contains("Plugin.CapabilitiesRequired", codes);
    }

    [Fact]
    public void ValidateRejectsIncompatibleContractAndPlatformVersions()
    {
        var validator = new PluginManifestValidator(new PluginCompatibilityOptions("1.0.0", "1.0.0"));
        var manifest = CreateManifest(
            contractVersion: "2.0.0",
            minimumPlatformVersion: "2.0.0");

        var report = validator.Validate(manifest);
        var codes = report.Issues.Select(issue => issue.Code).ToArray();

        Assert.False(report.IsValid);
        Assert.Contains("Plugin.ContractVersionUnsupported", codes);
        Assert.Contains("Plugin.PlatformVersionIncompatible", codes);
    }

    [Fact]
    public void ValidateRejectsDuplicateCapabilities()
    {
        var validator = new PluginManifestValidator();
        var manifest = CreateManifest(capabilities: ["device.scanner", "device.scanner"]);

        var report = validator.Validate(manifest);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Plugin.CapabilityDuplicate");
    }

    [Fact]
    public void ValidateAcceptsManifestWithDeviceCommandDefinitions()
    {
        var validator = new PluginManifestValidator();
        var manifest = CreateManifest(deviceCommands:
        [
            new PluginDeviceCommandDefinition(
                "device.scanner:scan",
                "device.scanner",
                "Scan",
                TimeoutMilliseconds: 5000)
        ]);

        var report = validator.Validate(manifest);

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void ValidateAcceptsManifestWithProcessCommandDefinitions()
    {
        var validator = new PluginManifestValidator();
        var manifest = CreateManifest(
            kind: PluginKind.ProcessNode,
            capabilities: ["process.vision"],
            processCommands:
            [
                new PluginProcessCommandDefinition(
                    "process.vision:inspect",
                    "process.vision",
                    "Inspect",
                    TimeoutMilliseconds: 5000)
            ]);

        var report = validator.Validate(manifest);

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void ValidateRejectsDeviceCommandReferencingUndeclaredCapability()
    {
        var validator = new PluginManifestValidator();
        var manifest = CreateManifest(deviceCommands:
        [
            new PluginDeviceCommandDefinition(
                "device.camera:capture",
                "device.camera",
                "Capture")
        ]);

        var report = validator.Validate(manifest);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Plugin.DeviceCommandCapabilityMissing");
    }

    [Fact]
    public void ValidateRejectsDuplicateAndInvalidDeviceCommands()
    {
        var validator = new PluginManifestValidator();
        var manifest = CreateManifest(deviceCommands:
        [
            new PluginDeviceCommandDefinition(
                "device.scanner:scan",
                "device.scanner",
                "Scan",
                TimeoutMilliseconds: 0,
                MaxRetries: -1),
            new PluginDeviceCommandDefinition(
                "device.scanner:scan",
                "device.scanner",
                "scan")
        ]);

        var report = validator.Validate(manifest);
        var codes = report.Issues.Select(issue => issue.Code).ToArray();

        Assert.False(report.IsValid);
        Assert.Contains("Plugin.DeviceCommandDuplicate", codes);
        Assert.Contains("Plugin.DeviceCommandNameDuplicate", codes);
        Assert.Contains("Plugin.DeviceCommandTimeoutInvalid", codes);
        Assert.Contains("Plugin.DeviceCommandRetriesInvalid", codes);
    }

    [Fact]
    public void ValidateRejectsInvalidProcessCommands()
    {
        var validator = new PluginManifestValidator();
        var manifest = CreateManifest(processCommands:
        [
            new PluginProcessCommandDefinition(
                "process.vision:inspect",
                "process.vision",
                "Inspect",
                TimeoutMilliseconds: 0,
                MaxRetries: -1),
            new PluginProcessCommandDefinition(
                "process.vision:inspect",
                "process.vision",
                "inspect")
        ]);

        var report = validator.Validate(manifest);
        var codes = report.Issues.Select(issue => issue.Code).ToArray();

        Assert.False(report.IsValid);
        Assert.Contains("Plugin.ProcessCommandKindInvalid", codes);
        Assert.Contains("Plugin.ProcessCommandCapabilityMissing", codes);
        Assert.Contains("Plugin.ProcessCommandDuplicate", codes);
        Assert.Contains("Plugin.ProcessCommandNameDuplicate", codes);
        Assert.Contains("Plugin.ProcessCommandTimeoutInvalid", codes);
        Assert.Contains("Plugin.ProcessCommandRetriesInvalid", codes);
    }

    private static PluginManifest CreateManifest(
        string contractVersion = "1.0.0",
        string minimumPlatformVersion = "1.0.0",
        PluginKind kind = PluginKind.DeviceDriver,
        IReadOnlyCollection<string>? capabilities = null,
        IReadOnlyCollection<PluginDeviceCommandDefinition>? deviceCommands = null,
        IReadOnlyCollection<PluginProcessCommandDefinition>? processCommands = null)
    {
        return new PluginManifest(
            "openlineops.fake-scanner",
            "Fake Scanner",
            "1.0.0",
            kind,
            "OpenLineOps.FakeScanner.dll",
            "OpenLineOps.FakeScanner.FakeScannerPlugin",
            capabilities ?? ["device.scanner"],
            contractVersion,
            minimumPlatformVersion,
            deviceCommands,
            processCommands);
    }
}
