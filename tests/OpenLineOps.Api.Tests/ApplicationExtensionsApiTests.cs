using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class ApplicationExtensionsApiTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ApplicationExtensionsApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Theory]
    [InlineData("/api/plugins/overview")]
    [InlineData("/api/plugins/lifecycle/start")]
    [InlineData("/api/plugins/lifecycle/stop")]
    [InlineData("/api/plugins/process-events")]
    public async Task RemovedGlobalPluginRoutesReturnNotFound(string route)
    {
        using var response = route.Contains("lifecycle", StringComparison.Ordinal)
            ? await _client.PostAsync(route, content: null)
            : await _client.GetAsync(route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ImportListValidateAndRemoveUseApplicationScopeAndExactFileHashes()
    {
        var workspace = await CreateWorkspaceAsync(defaultApplication: true);
        try
        {
            using var archive = CreatePackageArchive("plugin.api-preview", "api payload");
            using var import = await ImportAsync(
                workspace.ProjectId,
                workspace.ApplicationId,
                "api-preview",
                archive);
            using var importBody = await ReadJsonAsync(import);
            Assert.Equal(HttpStatusCode.Created, import.StatusCode);
            Assert.Equal("plugin.api-preview", importBody.RootElement.GetProperty("pluginId").GetString());
            Assert.Matches("^[0-9a-f]{64}$", importBody.RootElement.GetProperty("contentSha256").GetString()!);
            Assert.Equal(
                ["manifest.json", "plugin.dll"],
                importBody.RootElement.GetProperty("files").EnumerateArray()
                    .Select(file => file.GetProperty("relativePath").GetString()!)
                    .ToArray());
            Assert.All(importBody.RootElement.GetProperty("files").EnumerateArray(), file =>
            {
                Assert.True(file.GetProperty("sizeBytes").GetInt64() > 0);
                Assert.Matches("^[0-9a-f]{64}$", file.GetProperty("sha256").GetString()!);
            });

            var baseRoute = ExtensionRoute(workspace.ProjectId, workspace.ApplicationId);
            using var list = await _client.GetAsync(baseRoute);
            using var listBody = await ReadJsonAsync(list);
            using var validate = await _client.PostAsync($"{baseRoute}/validate", content: null);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
            Assert.Single(listBody.RootElement.EnumerateArray());

            using var remove = await _client.DeleteAsync($"{baseRoute}/plugin.api-preview");
            using var afterRemove = await _client.GetAsync(baseRoute);
            using var afterRemoveBody = await ReadJsonAsync(afterRemove);
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
            Assert.Empty(afterRemoveBody.RootElement.EnumerateArray());
        }
        finally
        {
            DeleteWorkspace(workspace.RootPath);
        }
    }

    [Fact]
    public async Task ExtensionInventoryDoesNotLeakAcrossApplications()
    {
        var first = await CreateWorkspaceAsync(defaultApplication: true);
        var second = await CreateWorkspaceAsync(defaultApplication: true);
        try
        {
            using var archive = CreatePackageArchive("plugin.isolated", "isolated payload");
            using var import = await ImportAsync(
                first.ProjectId,
                first.ApplicationId,
                "isolated",
                archive);
            Assert.Equal(HttpStatusCode.Created, import.StatusCode);

            using var firstList = await _client.GetAsync(ExtensionRoute(
                first.ProjectId,
                first.ApplicationId));
            using var firstBody = await ReadJsonAsync(firstList);
            using var secondList = await _client.GetAsync(ExtensionRoute(
                second.ProjectId,
                second.ApplicationId));
            using var secondBody = await ReadJsonAsync(secondList);

            Assert.Single(firstBody.RootElement.EnumerateArray());
            Assert.Empty(secondBody.RootElement.EnumerateArray());
        }
        finally
        {
            DeleteWorkspace(first.RootPath);
            DeleteWorkspace(second.RootPath);
        }
    }

    [Fact]
    public async Task CompleteApplicationCopyPreservesExtensionWithoutFileRewrite()
    {
        var source = await CreateWorkspaceAsync(defaultApplication: true);
        var target = await CreateWorkspaceAsync(defaultApplication: false);
        try
        {
            using var archive = CreatePackageArchive("plugin.portable", "portable payload");
            using var import = await ImportAsync(
                source.ProjectId,
                source.ApplicationId,
                "portable",
                archive);
            Assert.Equal(HttpStatusCode.Created, import.StatusCode);

            var sourceApplication = Path.Combine(
                source.RootPath,
                "applications",
                source.ApplicationId);
            var targetApplication = Path.Combine(target.RootPath, "applications", "copied");
            CopyDirectory(sourceApplication, targetApplication);
            var before = FileInventory(targetApplication);
            var projectFilePath = Path.Combine(targetApplication, $"{source.ApplicationId}.oloapp");
            using var importApplication = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{target.ProjectId}/applications/import",
                new { projectFilePath });
            Assert.Equal(HttpStatusCode.OK, importApplication.StatusCode);

            using var list = await _client.GetAsync(ExtensionRoute(
                target.ProjectId,
                source.ApplicationId));
            using var body = await ReadJsonAsync(list);

            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Equal("plugin.portable", Assert.Single(body.RootElement.EnumerateArray())
                .GetProperty("pluginId").GetString());
            Assert.Equal(before, FileInventory(targetApplication));
        }
        finally
        {
            DeleteWorkspace(source.RootPath);
            DeleteWorkspace(target.RootPath);
        }
    }

    [Fact]
    public async Task ImportRejectsInvalidZipAndSymbolicLinkEntriesWithoutCommitting()
    {
        var workspace = await CreateWorkspaceAsync(defaultApplication: true);
        try
        {
            using var invalid = new MemoryStream("not-a-zip"u8.ToArray());
            using var invalidResponse = await ImportAsync(
                workspace.ProjectId,
                workspace.ApplicationId,
                "invalid",
                invalid);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

            using var linked = CreateSymbolicLinkArchive("plugin.link");
            using var linkedResponse = await ImportAsync(
                workspace.ProjectId,
                workspace.ApplicationId,
                "linked",
                linked);
            Assert.Equal(HttpStatusCode.BadRequest, linkedResponse.StatusCode);

            using var list = await _client.GetAsync(ExtensionRoute(
                workspace.ProjectId,
                workspace.ApplicationId));
            using var body = await ReadJsonAsync(list);
            Assert.Empty(body.RootElement.EnumerateArray());
        }
        finally
        {
            DeleteWorkspace(workspace.RootPath);
        }
    }

    [Fact]
    public async Task ValidateFailsWhenReferencedPackageContentIsTampered()
    {
        var workspace = await CreateWorkspaceAsync(defaultApplication: true);
        try
        {
            using var archive = CreatePackageArchive("plugin.tamper", "original payload");
            using var import = await ImportAsync(
                workspace.ProjectId,
                workspace.ApplicationId,
                "tamper",
                archive);
            Assert.Equal(HttpStatusCode.Created, import.StatusCode);
            await File.AppendAllTextAsync(
                Path.Combine(
                    workspace.RootPath,
                    "applications",
                    workspace.ApplicationId,
                    "plugins",
                    "tamper",
                    "plugin.dll"),
                "tampered");

            using var validate = await _client.PostAsync(
                $"{ExtensionRoute(workspace.ProjectId, workspace.ApplicationId)}/validate",
                content: null);

            Assert.Equal(HttpStatusCode.BadRequest, validate.StatusCode);
        }
        finally
        {
            DeleteWorkspace(workspace.RootPath);
        }
    }

    private async Task<TestWorkspace> CreateWorkspaceAsync(bool defaultApplication)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-extensions-{suffix}";
        var applicationId = $"application-extensions-{suffix}";
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "openlineops-extension-api",
            suffix);
        using var response = await _client.PostAsJsonAsync(
            "/api/automation-project-workspaces",
            new
            {
                projectId,
                displayName = "Extensions Test",
                projectPath = rootPath,
                defaultApplicationId = defaultApplication ? applicationId : null,
                defaultApplicationName = defaultApplication ? "Extensions Application" : null
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new TestWorkspace(projectId, applicationId, rootPath);
    }

    private async Task<HttpResponseMessage> ImportAsync(
        string projectId,
        string applicationId,
        string portableId,
        Stream archive)
    {
        archive.Position = 0;
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(portableId, Encoding.UTF8), "portableId");
        form.Add(new StreamContent(archive), "package", $"{portableId}.zip");
        return await _client.PostAsync(
            $"{ExtensionRoute(projectId, applicationId)}/import",
            form);
    }

    private static MemoryStream CreatePackageArchive(string pluginId, string payload)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "manifest.json", Manifest(pluginId));
            WriteEntry(zip, "plugin.dll", payload);
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateSymbolicLinkArchive(string pluginId)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "manifest.json", Manifest(pluginId));
            var link = zip.CreateEntry("plugin.dll");
            link.ExternalAttributes = unchecked((int)0xA0000000);
            using var writer = new StreamWriter(link.Open(), Encoding.UTF8, leaveOpen: false);
            writer.Write("target");
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false), leaveOpen: false);
        writer.Write(content);
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

    private static string ExtensionRoute(string projectId, string applicationId) =>
        $"/api/automation-projects/{projectId}/applications/{applicationId}/extensions";

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

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

    private static string[] FileInventory(string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => $"{Path.GetRelativePath(root, path)}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}")
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static void DeleteWorkspace(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record TestWorkspace(
        string ProjectId,
        string ApplicationId,
        string RootPath);
}
