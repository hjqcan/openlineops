using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
    private readonly OpenLineOpsApiWebApplicationFactory _factory;

    public ExternalProgramDefinitionTrialApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory;
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
}
