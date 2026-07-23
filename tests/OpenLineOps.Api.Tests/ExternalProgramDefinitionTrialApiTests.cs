using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Application.ExternalPrograms;

namespace OpenLineOps.Api.Tests;

public sealed class ExternalProgramDefinitionTrialApiTests
    : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private static readonly JsonSerializerOptions WebSerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly OpenLineOpsApiWebApplicationFactory _factory;

    public ExternalProgramDefinitionTrialApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DirectoryImportPersistsCanonicalNestedInventoryThroughHttpBoundary()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var workspace = await CreateWorkspaceAsync(client);
        try
        {
            var executable = Encoding.UTF8.GetBytes("executable");
            var support = Encoding.UTF8.GetBytes("support");
            var definition = new
            {
                resourceId = "program.vendor-helper",
                displayName = "Vendor Helper",
                capabilityId = "device.vendor-helper",
                commandName = "Run",
                launchKind = "ApplicationExecutable",
                entryPoint = "files/bin/vendor-helper.exe",
                providerKind = (string?)null,
                providerKey = (string?)null,
                argumentTemplates = new[] { "--serial", "{{input.serial}}" },
                inputMappings = new[]
                {
                    new { source = "$product.identity", target = "serial" },
                    new { source = "$product.model", target = "model" }
                },
                resultMappings = new[]
                {
                    new { sourcePath = "$.outcome", targetKey = "vendor.outcome", valueKind = "Text" }
                },
                outcomeMapping = new
                {
                    sourcePath = "$.outcome",
                    passedToken = "Passed",
                    failedToken = "Failed",
                    abortedToken = "Aborted"
                },
                permissionProfile = new
                {
                    profileName = "Restricted",
                    networkAccessAllowed = false,
                    allowedEnvironmentVariables = Array.Empty<string>()
                },
                executionLimits = new
                {
                    timeoutMilliseconds = 30_000L,
                    maximumProcessCount = 2,
                    maximumWorkingSetBytes = 256L * 1024 * 1024,
                    maximumCpuTimeMilliseconds = 30_000L,
                    maximumStandardOutputBytes = 1024 * 1024,
                    maximumStandardErrorBytes = 1024 * 1024,
                    maximumArtifactCount = 8,
                    maximumArtifactBytes = 4L * 1024 * 1024,
                    maximumTotalArtifactBytes = 16L * 1024 * 1024
                }
            };
            var manifest = new[]
            {
                new
                {
                    fieldName = "file-1",
                    resourceRelativePath = "files/bin/vendor-helper.exe",
                    sizeBytes = (long)executable.Length,
                    sha256 = Sha256(executable)
                },
                new
                {
                    fieldName = "file-2",
                    resourceRelativePath = "files/lib/shared.settings.json",
                    sizeBytes = (long)support.Length,
                    sha256 = Sha256(support)
                }
            };
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(definition)), "definition");
            form.Add(new StringContent(JsonSerializer.Serialize(manifest)), "uploadManifest");
            form.Add(new ByteArrayContent(executable), "file-1", "vendor-helper.exe");
            form.Add(new ByteArrayContent(support), "file-2", "shared.settings.json");

            using var response = await client.PostAsync(
                $"/api/automation-projects/{workspace.ProjectId}"
                    + $"/applications/{workspace.ApplicationId}/external-programs/directory-import",
                form);
            var responseText = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.Created,
                $"Expected a created directory import but received {(int)response.StatusCode}: {responseText}");
            var body = JsonSerializer.Deserialize<ImportedExternalProgramBody>(
                responseText,
                WebSerializerOptions);
            Assert.NotNull(body);
            Assert.Equal("ApplicationExecutable", body.LaunchKind);
            Assert.Equal("files/bin/vendor-helper.exe", body.EntryPoint);
            Assert.Equal(
                ["files/bin/vendor-helper.exe", "files/lib/shared.settings.json"],
                body.Files.Select(file => file.RelativePath).ToArray());
        }
        finally
        {
            if (Directory.Exists(workspace.RootPath))
            {
                Directory.Delete(workspace.RootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CollectionTrialExecutesCanonicalUnsavedDefinitionWithoutWorkspaceMutation()
    {
        var executor = new RecordingTrialExecutor();
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExternalProgramTrialExecutor>();
            services.AddSingleton<IExternalProgramTrialExecutor>(executor);
        }));
        using var client = factory.CreateAuthenticatedClient();
        var workspace = await CreateWorkspaceAsync(client);
        try
        {
            var before = FileInventory(workspace.RootPath);
            var route = $"/api/automation-projects/{workspace.ProjectId}"
                + $"/applications/{workspace.ApplicationId}/external-programs";
            using var response = await client.PostAsJsonAsync($"{route}/trial", new
            {
                definition = new
                {
                    resourceId = "extension-trial-loopback",
                    displayName = "Loopback protocol trial",
                    capabilityId = "device.loopback",
                    commandName = "Echo",
                    launchKind = "Provider",
                    entryPoint = (string?)null,
                    providerKind = "PluginCommand",
                    providerKey = "openlineops.samples.loopback-device",
                    argumentTemplates = Array.Empty<string>(),
                    inputMappings = new[]
                    {
                        new { source = "$product.identity", target = "identity" },
                        new { source = "$product.model", target = "model" }
                    },
                    resultMappings = new[]
                    {
                        new
                        {
                            sourcePath = "$.deviceInstanceId",
                            targetKey = "extension.trial.result",
                            valueKind = "Text"
                        }
                    },
                    outcomeMapping = new
                    {
                        sourcePath = "$.deviceInstanceId",
                        passedToken = "loopback-device-01",
                        failedToken = "Failed",
                        abortedToken = "Aborted"
                    },
                    permissionProfile = new
                    {
                        profileName = "Restricted",
                        networkAccessAllowed = false,
                        allowedEnvironmentVariables = Array.Empty<string>()
                    },
                    executionLimits = new
                    {
                        timeoutMilliseconds = 10_000L,
                        maximumProcessCount = 1,
                        maximumWorkingSetBytes = 128L * 1024 * 1024,
                        maximumCpuTimeMilliseconds = 10_000L,
                        maximumStandardOutputBytes = 1024 * 1024,
                        maximumStandardErrorBytes = 1024 * 1024,
                        maximumArtifactCount = 2,
                        maximumArtifactBytes = 1024L * 1024,
                        maximumTotalArtifactBytes = 2L * 1024 * 1024
                    }
                },
                inputs = new Dictionary<string, object>
                {
                    ["identity"] = new { kind = "Text", canonicalValue = "board-001" },
                    ["model"] = new { kind = "Text", canonicalValue = "sample-board" }
                }
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<ExternalProgramTrialBody>();
            Assert.NotNull(body);
            Assert.Equal("extension-trial-loopback", body.ResourceId);
            Assert.Equal("Completed", body.ExecutionStatus);
            Assert.Equal("Passed", body.Judgement);
            Assert.Matches("^[0-9a-f]{64}$", body.ContentSha256);
            Assert.Equal(body.ContentSha256, executor.Resource?.ContentSha256);
            Assert.Equal("board-001", executor.Request?.Inputs["identity"].CanonicalValue);

            using var list = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var persisted = await list.Content.ReadFromJsonAsync<ExternalProgramResourceBody[]>();
            Assert.Empty(persisted ?? []);
            Assert.Equal(before, FileInventory(workspace.RootPath));
        }
        finally
        {
            if (Directory.Exists(workspace.RootPath))
            {
                Directory.Delete(workspace.RootPath, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("uploadManifest")]
    public async Task DirectoryImportRejectsDuplicateSingletonMultipartValueWithBadRequest(string duplicatedField)
    {
        using var client = _factory.CreateAuthenticatedClient();
        var workspace = await CreateWorkspaceAsync(client);
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("{}"), "definition");
            form.Add(new StringContent("[]"), "uploadManifest");
            form.Add(new StringContent(duplicatedField == "definition" ? "{}" : "[]"), duplicatedField);
            form.Add(new ByteArrayContent([1]), "file-1", "helper.exe");

            using var response = await client.PostAsync(
                $"/api/automation-projects/{workspace.ProjectId}"
                    + $"/applications/{workspace.ApplicationId}/external-programs/directory-import",
                form);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            if (Directory.Exists(workspace.RootPath))
            {
                Directory.Delete(workspace.RootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DirectoryImportRejectsUnexpectedMultipartValueWithBadRequest()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var workspace = await CreateWorkspaceAsync(client);
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("{}"), "definition");
            form.Add(new StringContent("[]"), "uploadManifest");
            form.Add(new StringContent("unexpected"), "sourcePath");
            form.Add(new ByteArrayContent([1]), "file-1", "helper.exe");

            using var response = await client.PostAsync(
                $"/api/automation-projects/{workspace.ProjectId}"
                    + $"/applications/{workspace.ApplicationId}/external-programs/directory-import",
                form);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            if (Directory.Exists(workspace.RootPath))
            {
                Directory.Delete(workspace.RootPath, recursive: true);
            }
        }
    }

    private static async Task<TestWorkspace> CreateWorkspaceAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-draft-trial-{suffix}";
        var applicationId = $"application-draft-trial-{suffix}";
        var rootPath = Path.Combine(Path.GetTempPath(), "openlineops-draft-trial-api", suffix);
        using var response = await client.PostAsJsonAsync(
            "/api/automation-project-workspaces",
            new
            {
                projectId,
                displayName = "Draft Trial",
                projectPath = rootPath,
                defaultApplicationId = applicationId,
                defaultApplicationName = "Draft Trial Application"
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new TestWorkspace(projectId, applicationId, rootPath);
    }

    private static string[] FileInventory(string rootPath) =>
        Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(rootPath, path).Replace('\\', '/')}|"
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant())
            .Order(StringComparer.Ordinal)
            .ToArray();

    private sealed class RecordingTrialExecutor : IExternalProgramTrialExecutor
    {
        public ExternalProgramResource? Resource { get; private set; }

        public ExternalProgramProtocolTrialRequest? Request { get; private set; }

        public ValueTask<Result<ExternalProgramProtocolTrialResult>> ExecuteAsync(
            ProjectApplicationWorkspaceScope scope,
            ExternalProgramResource resource,
            ExternalProgramProtocolTrialRequest request,
            CancellationToken cancellationToken = default)
        {
            Resource = resource;
            Request = request;
            return ValueTask.FromResult(Result.Success(new ExternalProgramProtocolTrialResult(
                resource.ResourceId,
                resource.LaunchKind.ToString(),
                resource.ContentSha256,
                "Completed",
                "Passed",
                "{}",
                null,
                [])));
        }
    }

    private sealed record TestWorkspace(string ProjectId, string ApplicationId, string RootPath);

    private sealed record ExternalProgramTrialBody(
        string ResourceId,
        string ContentSha256,
        string ExecutionStatus,
        string Judgement);

    private sealed record ExternalProgramResourceBody(string ResourceId);

    private sealed record ImportedExternalProgramBody(
        string LaunchKind,
        string EntryPoint,
        IReadOnlyCollection<ImportedExternalProgramFileBody> Files);

    private sealed record ImportedExternalProgramFileBody(string RelativePath);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
