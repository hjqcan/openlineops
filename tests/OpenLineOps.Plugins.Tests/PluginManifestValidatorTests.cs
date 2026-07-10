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
    public void ValidateRejectsSurroundingWhitespaceInsteadOfNormalizingManifestValues()
    {
        var validator = new PluginManifestValidator();
        var manifest = CreateManifest(
            kind: PluginKind.ProcessNode,
            capabilities: [" process.vision "],
            deviceCommands:
            [
                new PluginDeviceCommandDefinition(
                    " device.scan ",
                    " process.vision ",
                    " Scan ")
            ],
            processCommands:
            [
                new PluginProcessCommandDefinition(
                    " process.inspect ",
                    " process.vision ",
                    " Inspect ")
            ]) with
        {
            Id = " openlineops.fake-scanner ",
            Name = " Fake Scanner ",
            Version = " 1.0.0 ",
            EntryAssembly = " OpenLineOps.FakeScanner.dll ",
            EntryType = " OpenLineOps.FakeScanner.FakeScannerPlugin ",
            ContractVersion = " 1.0.0 ",
            MinimumPlatformVersion = " 1.0.0 ",
            RuntimeIdentifier = " any ",
            AbiVersion = " openlineops.plugin-abi/1 "
        };

        var report = validator.Validate(manifest);
        var codes = report.Issues.Select(issue => issue.Code).ToHashSet(StringComparer.Ordinal);

        Assert.False(report.IsValid);
        Assert.Contains("Plugin.ManifestIdNonCanonical", codes);
        Assert.Contains("Plugin.NameNonCanonical", codes);
        Assert.Contains("Plugin.VersionNonCanonical", codes);
        Assert.Contains("Plugin.EntryAssemblyNonCanonical", codes);
        Assert.Contains("Plugin.EntryTypeNonCanonical", codes);
        Assert.Contains("Plugin.ContractVersionNonCanonical", codes);
        Assert.Contains("Plugin.MinimumPlatformVersionNonCanonical", codes);
        Assert.Contains("Plugin.RuntimeIdentifierNonCanonical", codes);
        Assert.Contains("Plugin.AbiVersionNonCanonical", codes);
        Assert.Contains("Plugin.CapabilityNonCanonical", codes);
        Assert.Contains("Plugin.DeviceCommandIdNonCanonical", codes);
        Assert.Contains("Plugin.DeviceCommandCapabilityNonCanonical", codes);
        Assert.Contains("Plugin.DeviceCommandNameNonCanonical", codes);
        Assert.Contains("Plugin.ProcessCommandIdNonCanonical", codes);
        Assert.Contains("Plugin.ProcessCommandCapabilityNonCanonical", codes);
        Assert.Contains("Plugin.ProcessCommandNameNonCanonical", codes);
    }

    [Theory]
    [InlineData("bin\\Plugin.dll")]
    [InlineData("/Plugin.dll")]
    [InlineData("./Plugin.dll")]
    [InlineData("bin//Plugin.dll")]
    public void ValidateRejectsNonCanonicalEntryAssemblyPath(string entryAssembly)
    {
        var validator = new PluginManifestValidator();
        var manifest = CreateManifest() with { EntryAssembly = entryAssembly };

        var report = validator.Validate(manifest);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Plugin.EntryAssemblyPathNonCanonical");
    }

    [Fact]
    public void ValidateTreatsCapabilityAndCommandNameCasingAsExact()
    {
        var validator = new PluginManifestValidator();
        var manifest = CreateManifest(
            kind: PluginKind.ProcessNode,
            capabilities: ["device.scanner", "process.vision"],
            deviceCommands:
            [
                new PluginDeviceCommandDefinition("device.scanner:scan", "device.scanner", "Scan"),
                new PluginDeviceCommandDefinition("device.scanner:scan-lower", "device.scanner", "scan")
            ],
            processCommands:
            [
                new PluginProcessCommandDefinition("process.vision:inspect", "process.vision", "Inspect"),
                new PluginProcessCommandDefinition("process.vision:inspect-lower", "process.vision", "inspect")
            ]);

        var report = validator.Validate(manifest);

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void ValidateDoesNotCaseFoldCommandCapabilityReferences()
    {
        var validator = new PluginManifestValidator();
        var manifest = CreateManifest(deviceCommands:
        [
            new PluginDeviceCommandDefinition(
                "device.scanner:scan",
                "Device.Scanner",
                "Scan")
        ]);

        var report = validator.Validate(manifest);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "Plugin.DeviceCommandCapabilityMissing");
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
                "Scan")
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
                "Inspect")
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
