using System.Text.Json;
using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Infrastructure.Discovery;

namespace OpenLineOps.Plugins.Tests;

public sealed class FileSystemPluginPackageCatalogTests
{
    [Fact]
    public async Task DiscoverAsyncReadsOnlyCanonicalManifestFilesFromRootTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openlineops-plugins-{Guid.NewGuid():N}");
        var scannerDirectory = Path.Combine(root, "scanner");
        var exporterDirectory = Path.Combine(root, "exporter");
        var reporterDirectory = Path.Combine(root, "reporter");
        var sampleDirectory = Path.Combine(root, "sample");

        try
        {
            Directory.CreateDirectory(scannerDirectory);
            Directory.CreateDirectory(exporterDirectory);
            Directory.CreateDirectory(reporterDirectory);
            Directory.CreateDirectory(sampleDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(scannerDirectory, "openlineops-plugin.json"),
                """
                {
                  "id": "openlineops.fake-scanner",
                  "name": "Fake Scanner",
                  "version": "1.0.0",
                  "kind": "DeviceDriver",
                  "entryAssembly": "OpenLineOps.FakeScanner.dll",
                  "entryType": "OpenLineOps.FakeScanner.Plugin",
                  "capabilities": [ "device.scanner" ],
                  "contractVersion": "1.0.0",
                  "minimumPlatformVersion": "1.0.0"
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(exporterDirectory, "plugin.manifest.json"),
                """
                {
                  "id": "openlineops.csv-exporter",
                  "name": "CSV Exporter",
                  "version": "1.0.0",
                  "kind": "ReportExporter",
                  "entryAssembly": "OpenLineOps.CsvExporter.dll",
                  "entryType": "OpenLineOps.CsvExporter.Plugin",
                  "capabilities": [ "report.csv" ]
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(sampleDirectory, "manifest.json"),
                """
                {
                  "id": "openlineops.samples.loopback-device",
                  "name": "Loopback Device Sample",
                  "version": "0.1.0",
                  "kind": "DeviceDriver",
                  "entryAssembly": "OpenLineOps.SamplePlugins.LoopbackDevice.dll",
                  "entryType": "OpenLineOps.SamplePlugins.LoopbackDevice.LoopbackDevicePlugin",
                  "capabilities": [ "device.loopback" ]
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(reporterDirectory, "plugin.json"),
                """
                {
                  "id": "openlineops.reporter",
                  "name": "Reporter",
                  "version": "1.0.0",
                  "kind": "ReportExporter",
                  "entryAssembly": "Reporter.dll",
                  "entryType": "Reporter.Plugin",
                  "capabilities": [ "report.test" ]
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(scannerDirectory, "OpenLineOps.FakeScanner.dll"),
                "scanner-binary");
            await File.WriteAllTextAsync(
                Path.Combine(exporterDirectory, "OpenLineOps.CsvExporter.dll"),
                "exporter-binary");
            await File.WriteAllTextAsync(
                Path.Combine(sampleDirectory, "OpenLineOps.SamplePlugins.LoopbackDevice.dll"),
                "loopback-binary");
            await File.WriteAllTextAsync(Path.Combine(reporterDirectory, "Reporter.dll"), "reporter-binary");

            var catalog = new FileSystemPluginPackageCatalog(root);

            var packages = await catalog.DiscoverAsync();

            var package = Assert.Single(packages);
            Assert.Equal("openlineops.samples.loopback-device", package.Manifest.Id);
            Assert.Equal(PluginKind.DeviceDriver, package.Manifest.Kind);
            Assert.Equal("device.loopback", package.Manifest.Capabilities.Single());
            Assert.Equal(FileSystemPluginPackageCatalog.ManifestFileName, Path.GetFileName(package.ManifestPath));
            Assert.All(packages, package =>
            {
                Assert.True(package.RuntimeIdentity.IsComplete);
                Assert.Equal(64, package.PackageContentSha256.Length);
                Assert.Equal(64, package.ManifestSha256.Length);
                Assert.Equal(64, package.EntryAssemblySha256.Length);
                Assert.Contains(package.Files!, file =>
                    string.Equals(
                        file.RelativePath,
                        package.EntryAssemblyRelativePath,
                        StringComparison.Ordinal));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DiscoverAsyncReturnsEmptyCollectionWhenRootDoesNotExist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openlineops-missing-plugins-{Guid.NewGuid():N}");
        var catalog = new FileSystemPluginPackageCatalog(root);

        var packages = await catalog.DiscoverAsync();

        Assert.Empty(packages);
    }

    [Fact]
    public async Task DiscoverAsyncFullTreeHashChangesWhenSidecarChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openlineops-plugin-hash-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "manifest.json"),
                """
                {
                  "id": "openlineops.hash-test",
                  "name": "Hash Test",
                  "version": "1.0.0",
                  "kind": "ProcessNode",
                  "entryAssembly": "Plugin.dll",
                  "entryType": "HashTest.Plugin",
                  "capabilities": [ "process.hash" ]
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "Plugin.dll"), "binary-v1");
            var sidecarPath = Path.Combine(root, "dependency.dat");
            await File.WriteAllTextAsync(sidecarPath, "sidecar-v1");
            var catalog = new FileSystemPluginPackageCatalog(root);

            var first = Assert.Single(await catalog.DiscoverAsync());
            await File.WriteAllTextAsync(sidecarPath, "sidecar-updated");
            var second = Assert.Single(await catalog.DiscoverAsync());

            Assert.NotEqual(first.PackageContentSha256, second.PackageContentSha256);
            Assert.Equal(first.EntryAssemblySha256, second.EntryAssemblySha256);
            Assert.Equal(first.ManifestSha256, second.ManifestSha256);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DiscoverAsyncRejectsCaseAliasedManifestPropertyName()
    {
        var manifestJson = CanonicalManifestJson.Replace("\"id\":", "\"Id\":", StringComparison.Ordinal);

        await AssertManifestRejectedAsync<JsonException>(manifestJson);
    }

    [Fact]
    public async Task DiscoverAsyncRejectsUnknownManifestProperty()
    {
        var manifestJson = CanonicalManifestJson.Replace(
            "\"capabilities\": [ \"device.test\" ]",
            "\"capabilities\": [ \"device.test\" ], \"legacy\": true",
            StringComparison.Ordinal);

        await AssertManifestRejectedAsync<JsonException>(manifestJson);
    }

    [Theory]
    [InlineData("\"devicedriver\"")]
    [InlineData("1")]
    public async Task DiscoverAsyncRejectsNonCanonicalPluginKind(string kindJson)
    {
        var manifestJson = CanonicalManifestJson.Replace(
            "\"DeviceDriver\"",
            kindJson,
            StringComparison.Ordinal);

        await AssertManifestRejectedAsync<JsonException>(manifestJson);
    }

    [Theory]
    [InlineData("bin\\Plugin.dll")]
    [InlineData("/Plugin.dll")]
    [InlineData("./Plugin.dll")]
    [InlineData("bin//Plugin.dll")]
    [InlineData(" Plugin.dll")]
    public async Task DiscoverAsyncRejectsNonCanonicalEntryAssembly(string entryAssembly)
    {
        var manifestJson = CanonicalManifestJson.Replace(
            "\"Plugin.dll\"",
            JsonSerializer.Serialize(entryAssembly),
            StringComparison.Ordinal);

        await AssertManifestRejectedAsync<InvalidDataException>(manifestJson);
    }

    private static async Task AssertManifestRejectedAsync<TException>(string manifestJson)
        where TException : Exception
    {
        var root = Path.Combine(Path.GetTempPath(), $"openlineops-plugin-invalid-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), manifestJson);
            await File.WriteAllTextAsync(Path.Combine(root, "Plugin.dll"), "plugin-binary");

            var catalog = new FileSystemPluginPackageCatalog(root);

            await Assert.ThrowsAsync<TException>(async () =>
            {
                _ = await catalog.DiscoverAsync();
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private const string CanonicalManifestJson = """
        {
          "id": "openlineops.test",
          "name": "Test Plugin",
          "version": "1.0.0",
          "kind": "DeviceDriver",
          "entryAssembly": "Plugin.dll",
          "entryType": "Test.Plugin",
          "capabilities": [ "device.test" ]
        }
        """;
}
