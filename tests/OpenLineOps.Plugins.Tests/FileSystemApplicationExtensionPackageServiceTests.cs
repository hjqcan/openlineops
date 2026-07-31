using System.IO.Compression;
using System.Text;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Plugins.Application.Extensions;
using OpenLineOps.Plugins.Application.Validation;
using OpenLineOps.Plugins.Infrastructure.Discovery;
using OpenLineOps.Plugins.Infrastructure.Extensions;
using OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;

namespace OpenLineOps.Plugins.Tests;

public sealed class FileSystemApplicationExtensionPackageServiceTests
{
    [Fact]
    public async Task ImportListAndValidateReturnExactSortedFileHashPreview()
    {
        using var workspace = await TestWorkspace.CreateAsync();
        using var archive = CreatePackageArchive("plugin.preview", "preview payload");
        var service = workspace.CreateService();

        var imported = await service.ImportAsync(
            workspace.Scope,
            new ImportApplicationExtensionPackageRequest(
                "preview-package",
                archive,
                archive.Length));
        var listed = await service.ListAsync(workspace.Scope);
        var validated = await service.ValidateAsync(workspace.Scope);

        Assert.True(imported.IsSuccess);
        Assert.True(listed.IsSuccess);
        Assert.True(validated.IsSuccess);
        var details = Assert.Single(listed.Value);
        Assert.Equal("plugin.preview", details.Reference.PluginId);
        Assert.Equal(details.Reference.ContentSha256, details.Package.PackageContentSha256);
        Assert.Equal(
            ["manifest.json", "plugin.dll"],
            details.Package.Files!.Select(file => file.RelativePath).ToArray());
        Assert.All(details.Package.Files!, file =>
        {
            Assert.True(file.SizeBytes > 0);
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
        });
    }

    [Fact]
    public async Task ImportReferenceFailureRollsBackPackageDirectoryAndManifest()
    {
        using var workspace = await TestWorkspace.CreateAsync();
        workspace.ReferenceStore.FailNextReplace = true;
        using var archive = CreatePackageArchive("plugin.rollback", "rollback payload");
        var service = workspace.CreateService();

        var result = await service.ImportAsync(
            workspace.Scope,
            new ImportApplicationExtensionPackageRequest(
                "rollback-package",
                archive,
                archive.Length));

        Assert.True(result.IsFailure);
        Assert.Empty(await workspace.ManifestStore.ReadAsync(workspace.Scope));
        Assert.False(Directory.Exists(Path.Combine(
            workspace.Scope.PluginsRootPath,
            "rollback-package")));
        Assert.Empty(Directory.EnumerateDirectories(
            workspace.Scope.PluginsRootPath,
            ".olo-import-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task RemoveReferenceFailureRestoresExactPackageAndReference()
    {
        using var workspace = await TestWorkspace.CreateAsync();
        using var archive = CreatePackageArchive("plugin.remove-rollback", "original payload");
        var service = workspace.CreateService();
        var imported = await service.ImportAsync(
            workspace.Scope,
            new ImportApplicationExtensionPackageRequest(
                "remove-package",
                archive,
                archive.Length));
        Assert.True(imported.IsSuccess);
        var originalHash = imported.Value.Reference.ContentSha256;
        workspace.ReferenceStore.FailNextReplace = true;

        var removed = await service.RemoveAsync(workspace.Scope, "plugin.remove-rollback");

        Assert.True(removed.IsFailure);
        var reference = Assert.Single(await workspace.ManifestStore.ReadAsync(workspace.Scope));
        Assert.Equal(originalHash, reference.ContentSha256);
        var packagePath = Path.Combine(workspace.Scope.PluginsRootPath, "remove-package");
        Assert.True(Directory.Exists(packagePath));
        var package = await FileSystemPluginPackageInspector.InspectAsync(packagePath);
        Assert.Equal(originalHash, package.PackageContentSha256);
        Assert.Empty(Directory.EnumerateDirectories(
            workspace.Scope.PluginsRootPath,
            ".olo-remove-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ImportRejectsArchiveSymbolicLinkBeforeExtraction()
    {
        using var workspace = await TestWorkspace.CreateAsync();
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(zip, "manifest.json", Manifest("plugin.link"));
            var link = zip.CreateEntry("plugin.dll", CompressionLevel.Fastest);
            link.ExternalAttributes = unchecked((int)0xA0000000);
            await using var stream = link.Open();
            await stream.WriteAsync("target"u8.ToArray());
        }

        archive.Position = 0;
        var result = await workspace.CreateService().ImportAsync(
            workspace.Scope,
            new ImportApplicationExtensionPackageRequest("link-package", archive, archive.Length));

        Assert.True(result.IsFailure);
        Assert.Contains("symbolic link", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await workspace.ManifestStore.ReadAsync(workspace.Scope));
    }

    [Fact]
    public async Task ImportRejectsCompressedArchiveWhoseExpandedFileExceedsHardLimit()
    {
        using var workspace = await TestWorkspace.CreateAsync();
        using var honestArchive = await CreateOversizedPackageArchiveAsync();
        var result = await workspace.CreateService().ImportAsync(
            workspace.Scope,
            new ImportApplicationExtensionPackageRequest(
                "bomb-package",
                honestArchive,
                honestArchive.Length));

        Assert.True(result.IsFailure);
        Assert.Contains("expanded size", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await workspace.ManifestStore.ReadAsync(workspace.Scope));
    }

    private static MemoryStream CreatePackageArchive(string pluginId, string payload)
    {
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntryAsync(zip, "manifest.json", Manifest(pluginId)).GetAwaiter().GetResult();
            WriteEntryAsync(zip, "plugin.dll", payload).GetAwaiter().GetResult();
        }

        archive.Position = 0;
        return archive;
    }

    private static async Task<MemoryStream> CreateOversizedPackageArchiveAsync()
    {
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(zip, "manifest.json", Manifest("plugin.bomb"));
            var entry = zip.CreateEntry("plugin.dll", CompressionLevel.SmallestSize);
            await using var stream = entry.Open();
            var buffer = new byte[1024 * 1024];
            for (var written = 0L;
                 written <= FileSystemApplicationExtensionPackageService.MaximumFileBytes;
                 written += buffer.Length)
            {
                await stream.WriteAsync(buffer);
            }
        }

        archive.Position = 0;
        return archive;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(value);
        await stream.WriteAsync(bytes);
    }

    private static string Manifest(string pluginId) => $$"""
        {
          "id": "{{pluginId}}",
          "name": "{{pluginId}}",
          "version": "1.0.0",
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
        """;

    private sealed class TestWorkspace : IDisposable
    {
        private TestWorkspace(
            string root,
            ProjectApplicationWorkspaceScope scope,
            FileSystemAutomationProjectManifestStore manifestStore,
            FailingReferenceStore referenceStore)
        {
            Root = root;
            Scope = scope;
            ManifestStore = manifestStore;
            ReferenceStore = referenceStore;
        }

        public string Root { get; }

        public ProjectApplicationWorkspaceScope Scope { get; }

        public FileSystemAutomationProjectManifestStore ManifestStore { get; }

        public FailingReferenceStore ReferenceStore { get; }

        public static async Task<TestWorkspace> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "openlineops-extension-service",
                Guid.NewGuid().ToString("N"));
            var scope = new ProjectApplicationWorkspaceScope(
                "project.test",
                "application.test",
                root,
                "applications/test/test.oloapp");
            Directory.CreateDirectory(scope.PluginsRootPath);
            await File.WriteAllTextAsync(
                scope.ApplicationProjectFilePath,
                """
                {
                  "schemaVersion": "openlineops.automation-application",
                  "formatVersion": 1,
                  "kind": "OpenLineOps.AutomationApplication",
                  "product": "OpenLineOps",
                  "applicationId": "application.test",
                  "displayName": "Application Test",
                  "resourceLayoutVersion": 1,
                  "topologyId": null,
                  "processDefinitionIds": [],
                  "pluginPackageReferences": []
                }
                """);
            var manifestStore = new FileSystemAutomationProjectManifestStore();
            return new TestWorkspace(
                root,
                scope,
                manifestStore,
                new FailingReferenceStore(manifestStore));
        }

        public FileSystemApplicationExtensionPackageService CreateService()
        {
            var validator = new PluginManifestValidator();
            return new FileSystemApplicationExtensionPackageService(
                new FileSystemPluginPackageCatalog(ReferenceStore),
                ReferenceStore,
                validator);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FailingReferenceStore(
        IProjectApplicationPluginPackageReferenceStore inner)
        : IProjectApplicationPluginPackageReferenceStore
    {
        public bool FailNextReplace { get; set; }

        public ValueTask<IReadOnlyCollection<ProjectApplicationPluginPackageReference>> ReadAsync(
            ProjectApplicationWorkspaceScope scope,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(scope, cancellationToken);

        public ValueTask ReplaceAsync(
            ProjectApplicationWorkspaceScope scope,
            IReadOnlyCollection<ProjectApplicationPluginPackageReference> references,
            CancellationToken cancellationToken = default)
        {
            if (FailNextReplace)
            {
                FailNextReplace = false;
                throw new IOException("Injected atomic manifest replacement failure.");
            }

            return inner.ReplaceAsync(scope, references, cancellationToken);
        }
    }
}
