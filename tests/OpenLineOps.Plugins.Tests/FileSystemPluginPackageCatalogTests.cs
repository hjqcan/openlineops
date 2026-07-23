using OpenLineOps.Plugins.Infrastructure.Discovery;
using OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;

namespace OpenLineOps.Plugins.Tests;

public sealed class FileSystemPluginPackageCatalogTests
{
    [Fact]
    public async Task DiscoverAsyncReadsOnlyExactApplicationReferencesAndRejectsTampering()
    {
        using var workspace = await TestApplicationWorkspace.CreateAsync("project-a", "application-a");
        var package = await workspace.CreatePackageAsync(
            "vision-package",
            "plugin.vision",
            "1.0.0",
            "payload-a");
        await workspace.WriteReferencesAsync(package.Reference);
        var catalog = new FileSystemPluginPackageCatalog(workspace.ManifestStore);

        var discovered = Assert.Single(await catalog.DiscoverAsync(workspace.Scope));

        Assert.Equal("plugin.vision", discovered.Manifest.Id);
        Assert.Equal(package.Reference.ContentSha256, discovered.PackageContentSha256);
        await File.AppendAllTextAsync(Path.Combine(package.PackagePath, "plugin.dll"), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await catalog.DiscoverAsync(workspace.Scope));
    }

    [Fact]
    public async Task DiscoverAsyncFailsWhenReferencedPackageIsMissing()
    {
        using var workspace = await TestApplicationWorkspace.CreateAsync("project-a", "application-a");
        await workspace.WriteReferencesAsync(new ProjectApplicationPluginPackageReference(
            "plugin.missing",
            "1.0.0",
            ProjectApplicationPluginPackageReferenceContract.ManifestPath("missing"),
            new string('a', 64)));
        var catalog = new FileSystemPluginPackageCatalog(workspace.ManifestStore);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
            await catalog.DiscoverAsync(workspace.Scope));
    }

    [Fact]
    public async Task CompleteApplicationCopyDiscoversWithoutPackageFileRewrite()
    {
        using var source = await TestApplicationWorkspace.CreateAsync("project-a", "application-copy");
        var package = await source.CreatePackageAsync(
            "vendor-package",
            "plugin.vendor",
            "4.2.0",
            "vendor-payload");
        await source.WriteReferencesAsync(package.Reference);
        var sourceFiles = await SnapshotFilesAsync(source.Scope.ApplicationRootPath);

        using var target = await TestApplicationWorkspace.CreateAsync("project-b", "application-copy");
        Directory.Delete(target.Scope.ApplicationRootPath, recursive: true);
        CopyDirectory(source.Scope.ApplicationRootPath, target.Scope.ApplicationRootPath);
        var targetFiles = await SnapshotFilesAsync(target.Scope.ApplicationRootPath);
        var catalog = new FileSystemPluginPackageCatalog(target.ManifestStore);

        var discovered = Assert.Single(await catalog.DiscoverAsync(target.Scope));

        Assert.Equal(package.Reference.ContentSha256, discovered.PackageContentSha256);
        Assert.Equal(sourceFiles, targetFiles);
    }

    [Fact]
    public async Task SamePluginIdWithDifferentHashesRemainsIsolatedByApplication()
    {
        using var first = await TestApplicationWorkspace.CreateAsync("project", "application-a");
        using var second = await TestApplicationWorkspace.CreateAsync("project", "application-b");
        var firstPackage = await first.CreatePackageAsync(
            "shared-provider",
            "plugin.shared",
            "1.0.0",
            "first-content");
        var secondPackage = await second.CreatePackageAsync(
            "shared-provider",
            "plugin.shared",
            "1.0.0",
            "second-content");
        await first.WriteReferencesAsync(firstPackage.Reference);
        await second.WriteReferencesAsync(secondPackage.Reference);

        var firstDiscovered = Assert.Single(await new FileSystemPluginPackageCatalog(first.ManifestStore)
            .DiscoverAsync(first.Scope));
        var secondDiscovered = Assert.Single(await new FileSystemPluginPackageCatalog(second.ManifestStore)
            .DiscoverAsync(second.Scope));

        Assert.NotEqual(firstDiscovered.PackageContentSha256, secondDiscovered.PackageContentSha256);
        Assert.StartsWith(first.Scope.ApplicationRootPath, firstDiscovered.PackagePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(second.Scope.ApplicationRootPath, secondDiscovered.PackagePath, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string[]> SnapshotFilesAsync(string root)
    {
        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(path);
            files.Add($"{Path.GetRelativePath(root, path).Replace('\\', '/')}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))}");
        }

        return files.ToArray();
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private sealed class TestApplicationWorkspace : IDisposable
    {
        private TestApplicationWorkspace(string root, ProjectApplicationWorkspaceScope scope)
        {
            Root = root;
            Scope = scope;
        }

        public string Root { get; }

        public ProjectApplicationWorkspaceScope Scope { get; }

        public FileSystemAutomationProjectManifestStore ManifestStore { get; } = new();

        public static async Task<TestApplicationWorkspace> CreateAsync(
            string projectId,
            string applicationId)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "openlineops-scoped-plugin-catalog",
                Guid.NewGuid().ToString("N"));
            var scope = new ProjectApplicationWorkspaceScope(
                projectId,
                applicationId,
                root,
                $"applications/{applicationId}/{applicationId}.oloapp");
            Directory.CreateDirectory(scope.PluginsRootPath);
            await File.WriteAllTextAsync(
                scope.ApplicationProjectFilePath,
                $$"""
                {
                  "schemaVersion": "openlineops.automation-application",
                  "formatVersion": 1,
                  "kind": "OpenLineOps.AutomationApplication",
                  "product": "OpenLineOps",
                  "applicationId": "{{applicationId}}",
                  "displayName": "{{applicationId}}",
                  "resourceLayoutVersion": 1,
                  "topologyId": null,
                  "processDefinitionIds": [],
                  "pluginPackageReferences": []
                }
                """);
            return new TestApplicationWorkspace(root, scope);
        }

        public async Task<TestPackage> CreatePackageAsync(
            string portableId,
            string pluginId,
            string version,
            string payload)
        {
            var packagePath = Path.Combine(Scope.PluginsRootPath, portableId);
            Directory.CreateDirectory(packagePath);
            await File.WriteAllTextAsync(
                Path.Combine(packagePath, "manifest.json"),
                $$"""
                {
                  "id": "{{pluginId}}",
                  "name": "{{pluginId}}",
                  "version": "{{version}}",
                  "kind": "ProcessNode",
                  "entryAssembly": "plugin.dll",
                  "entryType": "Plugin.Entry",
                  "contractVersion": "1.0.0",
                  "minimumPlatformVersion": "1.0.0",
                  "runtimeIdentifier": "win-x64",
                  "abiVersion": "openlineops.plugin-abi/1",
                  "capabilities": ["process.test"],
                  "processCommands": [{
                    "id": "process.test:run",
                    "capability": "process.test",
                    "commandName": "Run",
                    "timeoutMilliseconds": 30000,
                    "maxRetries": 0
                  }]
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(packagePath, "plugin.dll"), payload);
            var descriptor = await FileSystemPluginPackageInspector.InspectAsync(packagePath);
            return new TestPackage(
                packagePath,
                new ProjectApplicationPluginPackageReference(
                    pluginId,
                    version,
                    ProjectApplicationPluginPackageReferenceContract.ManifestPath(portableId),
                    descriptor.PackageContentSha256));
        }

        public ValueTask WriteReferencesAsync(
            params ProjectApplicationPluginPackageReference[] references) =>
            ManifestStore.ReplaceAsync(Scope, references);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record TestPackage(
        string PackagePath,
        ProjectApplicationPluginPackageReference Reference);
}
