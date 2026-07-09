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
}
