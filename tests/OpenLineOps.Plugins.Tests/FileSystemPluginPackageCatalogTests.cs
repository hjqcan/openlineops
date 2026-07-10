using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.Plugins.Infrastructure.Discovery;

namespace OpenLineOps.Plugins.Tests;

public sealed class FileSystemPluginPackageCatalogTests
{
    [Fact]
    public async Task DiscoverAsyncReadsPluginManifestFilesFromRootTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openlineops-plugins-{Guid.NewGuid():N}");
        var scannerDirectory = Path.Combine(root, "scanner");
        var exporterDirectory = Path.Combine(root, "exporter");
        var sampleDirectory = Path.Combine(root, "sample");

        try
        {
            Directory.CreateDirectory(scannerDirectory);
            Directory.CreateDirectory(exporterDirectory);
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
                Path.Combine(scannerDirectory, "OpenLineOps.FakeScanner.dll"),
                "scanner-binary");
            await File.WriteAllTextAsync(
                Path.Combine(exporterDirectory, "OpenLineOps.CsvExporter.dll"),
                "exporter-binary");
            await File.WriteAllTextAsync(
                Path.Combine(sampleDirectory, "OpenLineOps.SamplePlugins.LoopbackDevice.dll"),
                "loopback-binary");

            var catalog = new FileSystemPluginPackageCatalog(root);

            var packages = await catalog.DiscoverAsync();

            Assert.Equal(3, packages.Count);
            Assert.Contains(packages, package =>
                package.Manifest.Id == "openlineops.fake-scanner"
                && package.Manifest.Kind == PluginKind.DeviceDriver
                && package.Manifest.Capabilities.Single() == "device.scanner");
            Assert.Contains(packages, package =>
                package.Manifest.Id == "openlineops.csv-exporter"
                && package.Manifest.Kind == PluginKind.ReportExporter
                && package.Manifest.ContractVersion == "1.0.0");
            Assert.Contains(packages, package =>
                package.Manifest.Id == "openlineops.samples.loopback-device"
                && package.Manifest.Kind == PluginKind.DeviceDriver
                && package.Manifest.Capabilities.Single() == "device.loopback"
                && Path.GetFileName(package.ManifestPath) == "manifest.json");
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
            await File.WriteAllTextAsync(sidecarPath, "sidecar-v2");
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
}
