using Microsoft.Extensions.Configuration;

namespace OpenLineOps.Agent.Tests;

public sealed class StationAgentContentCacheProvisioningCommandTests
{
    [Fact]
    public void CommandLineDefaultsToServiceHostMode()
    {
        var commandLine = StationAgentCommandLine.Parse([]);

        Assert.False(commandLine.ProvisionContentCache);
        Assert.Null(commandLine.RemoveContentCachePackageSha256);
        Assert.Empty(commandLine.ConfigurationArguments);
    }

    [Fact]
    public void CommandLineRemovesProvisioningSwitchAndPreservesConfigurationArguments()
    {
        var commandLine = StationAgentCommandLine.Parse(
        [
            "--OpenLineOps:WindowsServiceName",
            "OpenLineOpsAgent-LineA",
            StationAgentCommandLine.ProvisionContentCacheSwitch,
            "--OpenLineOps:Agent:PackageCacheDirectory",
            "C:\\ProgramData\\OpenLineOps\\StationCaches\\LineA\\content"
        ]);

        Assert.True(commandLine.ProvisionContentCache);
        Assert.Null(commandLine.RemoveContentCachePackageSha256);
        Assert.Equal(
        [
            "--OpenLineOps:WindowsServiceName",
            "OpenLineOpsAgent-LineA",
            "--OpenLineOps:Agent:PackageCacheDirectory",
            "C:\\ProgramData\\OpenLineOps\\StationCaches\\LineA\\content"
        ],
            commandLine.ConfigurationArguments);
    }

    [Fact]
    public void CommandLineRejectsDuplicateProvisioningSwitch()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentCommandLine.Parse(
            [
                StationAgentCommandLine.ProvisionContentCacheSwitch,
                StationAgentCommandLine.ProvisionContentCacheSwitch
            ]));

        Assert.Contains("only once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLineParsesProtectedPackageRemovalAndPreservesConfigurationArguments()
    {
        var contentSha256 = new string('a', 64);

        var commandLine = StationAgentCommandLine.Parse(
        [
            StationAgentCommandLine.RemoveContentCachePackageSwitch,
            contentSha256,
            "--OpenLineOps:WindowsServiceName",
            "OpenLineOpsAgent-LineA"
        ]);

        Assert.False(commandLine.ProvisionContentCache);
        Assert.Equal(contentSha256, commandLine.RemoveContentCachePackageSha256);
        Assert.Equal(
        [
            "--OpenLineOps:WindowsServiceName",
            "OpenLineOpsAgent-LineA"
        ],
            commandLine.ConfigurationArguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void CommandLineRejectsInvalidProtectedPackageRemovalHash(string value)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentCommandLine.Parse(
            [
                StationAgentCommandLine.RemoveContentCachePackageSwitch,
                value
            ]));

        Assert.Contains("lowercase SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLineRejectsMissingProtectedPackageRemovalHash()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentCommandLine.Parse(
            [
                StationAgentCommandLine.RemoveContentCachePackageSwitch
            ]));

        Assert.Contains("requires one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLineRejectsDuplicateProtectedPackageRemovalSwitch()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentCommandLine.Parse(
            [
                StationAgentCommandLine.RemoveContentCachePackageSwitch,
                new string('a', 64),
                StationAgentCommandLine.RemoveContentCachePackageSwitch,
                new string('b', 64)
            ]));

        Assert.Contains("only once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLineRejectsMutuallyExclusiveAdministrativeModes()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentCommandLine.Parse(
            [
                StationAgentCommandLine.ProvisionContentCacheSwitch,
                StationAgentCommandLine.RemoveContentCachePackageSwitch,
                new string('a', 64)
            ]));

        Assert.Contains("mutually exclusive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningOptionsRequireExplicitPackageCacheDirectory()
    {
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["OpenLineOps:WindowsServiceName"] = "OpenLineOpsAgent-LineA"
            });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentContentCacheProvisioningOptions.Load(configuration));

        Assert.Contains(
            "OpenLineOps:Agent:PackageCacheDirectory is required",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningOptionsRejectRelativeCachePath()
    {
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["OpenLineOps:WindowsServiceName"] = "OpenLineOpsAgent-LineA",
                ["OpenLineOps:Agent:PackageCacheDirectory"] = "cache\\content"
            });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentContentCacheProvisioningOptions.Load(configuration));

        Assert.Contains(
            "fully-qualified canonical path",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningOptionsRejectVolumeRootAsCachePath()
    {
        var volumeRoot = Path.GetPathRoot(Path.GetTempPath())
            ?? throw new InvalidOperationException("Test temporary path has no volume root.");
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["OpenLineOps:WindowsServiceName"] = "OpenLineOpsAgent-LineA",
                ["OpenLineOps:Agent:PackageCacheDirectory"] = volumeRoot
            });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentContentCacheProvisioningOptions.Load(configuration));

        Assert.Contains("canonical non-root directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningOptionsAcceptCanonicalAbsoluteCachePath()
    {
        var cachePath = Path.Combine(
            Path.GetTempPath().TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            "openlineops-cache",
            "content-anchor",
            "content");
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["OpenLineOps:WindowsServiceName"] = "OpenLineOpsAgent-LineA",
                ["OpenLineOps:Agent:PackageCacheDirectory"] = cachePath
            });

        var options = StationAgentContentCacheProvisioningOptions.Load(configuration);

        Assert.Equal("OpenLineOpsAgent-LineA", options.WindowsServiceName);
        Assert.Equal(cachePath, options.PackageCacheDirectory);
    }

    [Fact]
    public void ProvisioningOptionsNormalizesOneTrailingSeparator()
    {
        var cachePath = Path.Combine(
            Path.GetTempPath().TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            "openlineops-cache",
            "content") + Path.DirectorySeparatorChar;
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["OpenLineOps:WindowsServiceName"] = "OpenLineOpsAgent-LineA",
                ["OpenLineOps:Agent:PackageCacheDirectory"] = cachePath
            });

        var options = StationAgentContentCacheProvisioningOptions.Load(configuration);

        Assert.Equal(cachePath[..^1], options.PackageCacheDirectory);
    }

    [Fact]
    public void ProvisioningOptionsRejectRepeatedTrailingSeparators()
    {
        var cachePath = Path.Combine(
            Path.GetTempPath().TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            "openlineops-cache",
            "content") + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar;
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["OpenLineOps:WindowsServiceName"] = "OpenLineOpsAgent-LineA",
                ["OpenLineOps:Agent:PackageCacheDirectory"] = cachePath
            });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentContentCacheProvisioningOptions.Load(configuration));

        Assert.Contains(
            "repeated trailing directory separators",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningOptionsRejectDotSegmentAlias()
    {
        var cachePath = string.Join(
            Path.DirectorySeparatorChar,
            Path.GetTempPath().TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            "openlineops-cache",
            ".",
            "content");
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["OpenLineOps:WindowsServiceName"] = "OpenLineOpsAgent-LineA",
                ["OpenLineOps:Agent:PackageCacheDirectory"] = cachePath
            });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentContentCacheProvisioningOptions.Load(configuration));

        Assert.Contains("dot-segment", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningOptionsRejectWindowsUncCachePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["OpenLineOps:WindowsServiceName"] = "OpenLineOpsAgent-LineA",
                ["OpenLineOps:Agent:PackageCacheDirectory"] =
                    "\\\\localhost\\OpenLineOpsCache\\content"
            });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentContentCacheProvisioningOptions.Load(configuration));

        Assert.Contains("UNC and device paths", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningOptionsAcceptLowercaseWindowsDriveLetter()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cachePath = Path.Combine(
            Path.GetTempPath().TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            "openlineops-cache",
            "content-anchor",
            "content");
        var lowercaseDrivePath = char.ToLowerInvariant(cachePath[0]) + cachePath[1..];
        var configuration = Configuration(
            new Dictionary<string, string?>
            {
                ["OpenLineOps:WindowsServiceName"] = "OpenLineOpsAgent-LineA",
                ["OpenLineOps:Agent:PackageCacheDirectory"] = lowercaseDrivePath
            });

        var options = StationAgentContentCacheProvisioningOptions.Load(configuration);

        Assert.Equal(lowercaseDrivePath, options.PackageCacheDirectory);
    }

    [Fact]
    public void ExecuteFailsClosedOutsideWindowsBeforeProvisioning()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = Assert.Throws<PlatformNotSupportedException>(() =>
            StationAgentContentCacheProvisioningCommand.Execute(
                Configuration(new Dictionary<string, string?>())));

        Assert.Contains(
            "elevated Windows administrator",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovePackageFailsClosedOutsideWindowsBeforeRemoval()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = await Assert.ThrowsAsync<PlatformNotSupportedException>(async () =>
            await StationAgentContentCacheProvisioningCommand.RemovePackageAsync(
                Configuration(new Dictionary<string, string?>()),
                new string('a', 64)));

        Assert.Contains(
            "elevated Windows administrator",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(
        IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
