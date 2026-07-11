using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class AutomationProjectWorkspaceApiTests : IClassFixture<StationPackageWebApplicationFactory>
{
    private static readonly string[] SnapshotBlockVersionIds = ["block.motion.axis.move@1.0.0"];

    private readonly HttpClient _client;
    private readonly StationPackageWebApplicationFactory _factory;

    public AutomationProjectWorkspaceApiTests(StationPackageWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task ImportedApplicationPublishesSignedPackageInAnotherProjectWithoutFileRewrite()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sourceProjectId = $"project-portable-source-{suffix}";
        var targetProjectId = $"project-portable-target-{suffix}";
        var applicationId = $"application-portable-{suffix}";
        var topologyId = $"topology-portable-{suffix}";
        var layoutId = $"layout-portable-{suffix}";
        var processDefinitionId = $"process-portable-{suffix}";
        var processVersionId = $"{processDefinitionId}@1.0.0";
        var configurationSnapshotId = $"configuration-portable-{suffix}";
        var productionLineDefinitionId = $"line-scoped-{suffix}";
        var sourceDirectory = ProjectReleaseTestDirectory($"source-{suffix}");
        var targetDirectory = ProjectReleaseTestDirectory($"target-{suffix}");
        try
        {
            await CreateScopedReleaseSourceAsync(
                sourceDirectory,
                sourceProjectId,
                applicationId,
                topologyId,
                layoutId,
                processDefinitionId,
                processVersionId,
                configurationSnapshotId,
                $"capability-portable-{suffix}",
                $"binding-portable-{suffix}",
                suffix);
            using (var saveSourceManifest = await _client.PutAsync(
                       $"/api/automation-projects/{sourceProjectId}/manifest",
                       content: null))
            {
                Assert.Equal(HttpStatusCode.OK, saveSourceManifest.StatusCode);
            }
            using (var createTarget = await _client.PostAsJsonAsync(
                       "/api/automation-project-workspaces",
                       new
                       {
                           projectId = targetProjectId,
                           displayName = "Portable Target",
                           projectPath = targetDirectory,
                           defaultApplicationId = (string?)null,
                           defaultApplicationName = (string?)null
                       }))
            {
                Assert.Equal(HttpStatusCode.Created, createTarget.StatusCode);
            }

            var sourceApplication = Path.Combine(sourceDirectory, "applications", applicationId);
            var targetApplication = Path.Combine(targetDirectory, "applications", "imported-portable");
            CopyDirectory(sourceApplication, targetApplication);
            var before = ApplicationFileInventory(targetApplication);
            var applicationProjectFile = Path.Combine(targetApplication, $"{applicationId}.oloapp");
            using (var import = await _client.PostAsJsonAsync(
                       $"/api/automation-projects/{targetProjectId}/applications/import",
                       new { projectFilePath = applicationProjectFile }))
            {
                Assert.Equal(HttpStatusCode.OK, import.StatusCode);
            }

            using var publish = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{targetProjectId}/snapshots",
                new
                {
                    snapshotId = $"snapshot-portable-{suffix}",
                    applicationId,
                    productionLineDefinitionId
                });
            using var publishBody = await ReadJsonAsync(publish);
            Assert.True(
                publish.StatusCode == HttpStatusCode.Created,
                publishBody.RootElement.ToString());
            Assert.Equal(before, ApplicationFileInventory(targetApplication));

            var packagePath = Directory
                .EnumerateFiles(_factory.DistributionDirectory, "*.olopkg")
                .Single(path => PackageBelongsTo(path, targetProjectId, applicationId));
            var packageManifest = ReadPackageManifest(packagePath);
            using (packageManifest)
            {
                Assert.Equal(
                    "station.main",
                    packageManifest.RootElement.GetProperty("stationSystemId").GetString());
                Assert.Equal(
                    "RSA-PSS-SHA256",
                    ReadPackageSignatureAlgorithm(packagePath));
            }
            Assert.Contains(
                Directory.EnumerateFiles(_factory.DeploymentCatalogDirectory, "*.json"),
                path => File.ReadAllText(path).Contains(targetProjectId, StringComparison.Ordinal));
        }
        finally
        {
            DeleteProjectDirectory(sourceDirectory);
            DeleteProjectDirectory(targetDirectory);
        }
    }

    [Fact]
    public async Task ProjectWorkspaceManifestCanBeCreatedSavedAndOpened()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-workspace-manifest-{suffix}";
        var applicationId = $"application-workspace-{suffix}";
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-api-project-workspace-tests",
            suffix);

        try
        {
            using var createWorkspaceResponse = await _client.PostAsJsonAsync(
                "/api/automation-project-workspaces",
                new
                {
                    projectId,
                    displayName = "Workspace Manifest Project",
                    projectPath = projectDirectory,
                    defaultApplicationId = applicationId,
                    defaultApplicationName = "Workspace Application"
                });
            using var createWorkspaceBody = await ReadJsonAsync(createWorkspaceResponse);

            Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);
            Assert.Equal(
                projectId,
                createWorkspaceBody.RootElement.GetProperty("project").GetProperty("projectId").GetString());
            var projectFilePath = createWorkspaceBody.RootElement.GetProperty("manifestPath").GetString();
            Assert.NotNull(projectFilePath);
            Assert.EndsWith(".oloproj", projectFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(projectFilePath));
            var applicationProjectRelativePath = createWorkspaceBody.RootElement
                .GetProperty("project")
                .GetProperty("applications")[0]
                .GetProperty("projectFilePath")
                .GetString();
            Assert.NotNull(applicationProjectRelativePath);
            Assert.EndsWith(".oloapp", applicationProjectRelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(
                projectDirectory,
                applicationProjectRelativePath.Replace('/', Path.DirectorySeparatorChar))));
            using (var projectFile = JsonDocument.Parse(await File.ReadAllTextAsync(projectFilePath)))
            {
                Assert.False(projectFile.RootElement.TryGetProperty("projectPath", out _));
            }

            using var saveManifestResponse = await _client.PutAsync(
                $"/api/automation-projects/{projectId}/manifest",
                content: null);
            using var saveManifestBody = await ReadJsonAsync(saveManifestResponse);

            Assert.Equal(HttpStatusCode.OK, saveManifestResponse.StatusCode);
            Assert.Equal(
                applicationId,
                saveManifestBody.RootElement
                    .GetProperty("manifest")
                    .GetProperty("applications")[0]
                    .GetProperty("applicationId")
                    .GetString());

            using var openWorkspaceResponse = await _client.PostAsJsonAsync(
                "/api/automation-project-workspaces/open",
                new
                {
                    projectPath = projectFilePath
                });
            using var openWorkspaceBody = await ReadJsonAsync(openWorkspaceResponse);

            Assert.Equal(HttpStatusCode.OK, openWorkspaceResponse.StatusCode);
            Assert.Equal(
                projectId,
                openWorkspaceBody.RootElement.GetProperty("project").GetProperty("projectId").GetString());
            Assert.Equal(
                applicationId,
                openWorkspaceBody.RootElement
                    .GetProperty("project")
                    .GetProperty("applications")[0]
                    .GetProperty("applicationId")
                    .GetString());
        }
        finally
        {
            if (Directory.Exists(projectDirectory))
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExistingApplicationProjectInsideWorkspaceCanBeImported()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-import-{suffix}";
        var applicationId = $"app-import-{suffix}";
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-api-application-import-tests",
            suffix);

        try
        {
            using var createResponse = await _client.PostAsJsonAsync(
                "/api/automation-project-workspaces",
                new
                {
                    projectId,
                    displayName = "Import Target",
                    projectPath = projectDirectory,
                    defaultApplicationId = (string?)null,
                    defaultApplicationName = (string?)null
                });
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

            var applicationDirectory = Path.Combine(
                projectDirectory,
                "applications",
                "copied-app");
            Directory.CreateDirectory(applicationDirectory);
            var applicationFilePath = Path.Combine(applicationDirectory, "copied.oloapp");
            await File.WriteAllTextAsync(
                applicationFilePath,
                $$"""
                {
                  "schemaVersion": "openlineops.automation-application",
                  "formatVersion": 1,
                  "kind": "OpenLineOps.AutomationApplication",
                  "product": "OpenLineOps",
                  "applicationId": "{{applicationId}}",
                  "displayName": "Copied Application",
                  "resourceLayoutVersion": 1,
                  "topologyId": null,
                  "processDefinitionIds": []
                }
                """);

            using var importResponse = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/applications/import",
                new { projectFilePath = applicationFilePath });
            using var importBody = await ReadJsonAsync(importResponse);

            Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
            var application = importBody.RootElement
                .GetProperty("project")
                .GetProperty("applications")[0];
            Assert.Equal(applicationId, application.GetProperty("applicationId").GetString());
            Assert.Equal(
                "applications/copied-app/copied.oloapp",
                application.GetProperty("projectFilePath").GetString());
        }
        finally
        {
            if (Directory.Exists(projectDirectory))
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProjectWorkspaceCompositionCanBeCreatedAndPublished()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-scoped-composition-{suffix}";
        var applicationId = $"application-scoped-composition-{suffix}";
        var topologyId = $"topology-scoped-composition-{suffix}";
        var layoutId = $"layout-scoped-composition-{suffix}";
        var processDefinitionId = $"process-scoped-composition-{suffix}";
        var processVersionId = $"{processDefinitionId}@1.0.0";
        var configurationSnapshotId = $"configuration-scoped-composition-{suffix}";
        var productionLineDefinitionId = $"line-scoped-{suffix}";
        var capabilityId = $"capability-scoped-composition-{suffix}";
        var bindingId = $"binding-scoped-composition-{suffix}";
        var snapshotId = $"snapshot-scoped-composition-{suffix}";
        var projectDirectory = ProjectReleaseTestDirectory(suffix);

        try
        {
            await CreateScopedReleaseSourceAsync(
                projectDirectory,
                projectId,
                applicationId,
                topologyId,
                layoutId,
                processDefinitionId,
                processVersionId,
                configurationSnapshotId,
                capabilityId,
                bindingId,
                suffix);

            using var publishResponse = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/snapshots",
                new
                {
                    snapshotId,
                    applicationId,
                    productionLineDefinitionId
                });
            using var publishBody = await ReadJsonAsync(publishResponse);

            Assert.Equal(HttpStatusCode.Created, publishResponse.StatusCode);
            Assert.Equal(snapshotId, publishBody.RootElement.GetProperty("activeSnapshotId").GetString());
            var snapshot = Assert.Single(publishBody.RootElement.GetProperty("snapshots").EnumerateArray());
            Assert.Equal(topologyId, snapshot.GetProperty("topologyId").GetString());
            Assert.Equal(
                productionLineDefinitionId,
                snapshot.GetProperty("productionLineDefinitionId").GetString());
            Assert.Equal(layoutId, Assert.Single(snapshot.GetProperty("layoutIds").EnumerateArray()).GetString());
            Assert.False(snapshot.TryGetProperty("processDefinitionId", out _));
            Assert.False(snapshot.TryGetProperty("processVersionId", out _));
            Assert.False(snapshot.TryGetProperty("configurationSnapshotId", out _));
            var binding = Assert.Single(snapshot.GetProperty("capabilityBindings").EnumerateArray());
            Assert.Equal(capabilityId, binding.GetProperty("capabilityId").GetString());
            Assert.Equal(bindingId, binding.GetProperty("bindingId").GetString());
            var targets = snapshot.GetProperty("targetReferences")
                .EnumerateArray()
                .Select(target => (
                    Kind: target.GetProperty("kind").GetString(),
                    TargetId: target.GetProperty("targetId").GetString()))
                .ToArray();
            Assert.Equal(3, targets.Length);
            Assert.Equal(targets.Length, targets.Distinct().Count());
            Assert.Equal(
                targets.OrderBy(target => target.Kind, StringComparer.Ordinal)
                    .ThenBy(target => target.TargetId, StringComparer.Ordinal)
                    .ToArray(),
                targets);
            Assert.Contains(targets, target => target is { Kind: "System", TargetId: "station.main" });
            Assert.Contains(targets, target => target is { Kind: "Capability" }
                && target.TargetId == capabilityId);
            Assert.Contains(targets, target => target is { Kind: "Driver" }
                && target.TargetId == bindingId);
            Assert.Empty(snapshot.GetProperty("blockVersionIds").EnumerateArray());
            AssertImmutableRelease(snapshot, projectDirectory);

            using var queryProjectResponse = await _client.GetAsync($"/api/automation-projects/{projectId}");
            using var queryProjectBody = await ReadJsonAsync(queryProjectResponse);
            Assert.Equal(HttpStatusCode.OK, queryProjectResponse.StatusCode);
            Assert.Equal(snapshotId, queryProjectBody.RootElement.GetProperty("activeSnapshotId").GetString());
        }
        finally
        {
            DeleteProjectDirectory(projectDirectory);
        }
    }

    [Fact]
    public async Task PublishedProjectSnapshotCanRunItsFrozenProductionLine()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-scoped-runtime-{suffix}";
        var applicationId = $"application-scoped-runtime-{suffix}";
        var topologyId = $"topology-scoped-runtime-{suffix}";
        var layoutId = $"layout-scoped-runtime-{suffix}";
        var processDefinitionId = $"process-scoped-runtime-{suffix}";
        const string processVersionId = "packaging-line-eol@1.0.0";
        var configurationSnapshotId = $"configuration-scoped-runtime-{suffix}";
        var productionLineDefinitionId = $"line-scoped-{suffix}";
        const string capabilityId = "device.scanner";
        var bindingId = $"binding-scoped-runtime-{suffix}";
        var snapshotId = $"snapshot-scoped-runtime-{suffix}";
        var projectDirectory = ProjectReleaseTestDirectory(suffix);

        try
        {
            await CreateScopedReleaseSourceAsync(
                projectDirectory,
                projectId,
                applicationId,
                topologyId,
                layoutId,
                processDefinitionId,
                processVersionId,
                configurationSnapshotId,
                capabilityId,
                bindingId,
                suffix);

            using var publishResponse = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/snapshots",
                new
                {
                    snapshotId,
                    applicationId,
                    productionLineDefinitionId
                });
            using var publishBody = await ReadJsonAsync(publishResponse);
            Assert.True(
                publishResponse.StatusCode == HttpStatusCode.Created,
                $"Snapshot publication returned {(int)publishResponse.StatusCode} "
                + $"{publishResponse.StatusCode}: {publishBody.RootElement.GetRawText()}");
            var snapshot = Assert.Single(publishBody.RootElement.GetProperty("snapshots").EnumerateArray());
            AssertImmutableRelease(snapshot, projectDirectory);

            var contextPath = $"/api/automation-projects/{projectId}/snapshots/{snapshotId}/production-run-context";
            using var contextResponse = await _client.GetAsync(contextPath);
            using var contextBody = await ReadJsonAsync(contextResponse);
            Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
            Assert.Equal(10, contextBody.RootElement.EnumerateObject().Count());
            Assert.False(contextBody.RootElement.TryGetProperty("releaseContentSha256", out _));
            Assert.False(contextBody.RootElement.TryGetProperty("releaseManifestPath", out _));
            Assert.Equal(projectId, contextBody.RootElement.GetProperty("projectId").GetString());
            Assert.Equal(applicationId, contextBody.RootElement.GetProperty("applicationId").GetString());
            Assert.Equal(snapshotId, contextBody.RootElement.GetProperty("snapshotId").GetString());
            Assert.Equal(topologyId, contextBody.RootElement.GetProperty("topologyId").GetString());
            Assert.Equal(
                productionLineDefinitionId,
                contextBody.RootElement.GetProperty("productionLineDefinitionId").GetString());
            var frozenProductModelId = contextBody.RootElement.GetProperty("productModelId").GetString();
            var frozenIdentityInputKey = contextBody.RootElement
                .GetProperty("productModelIdentityInputKey")
                .GetString();
            var frozenEntryStationSystemId = contextBody.RootElement
                .GetProperty("entryStationSystemId")
                .GetString();
            Assert.Equal($"product-scoped-{suffix}", frozenProductModelId);
            Assert.Equal("serialNumber", frozenIdentityInputKey);
            Assert.Equal("operation.main", contextBody.RootElement.GetProperty("entryOperationId").GetString());
            Assert.Equal("station.main", frozenEntryStationSystemId);
            Assert.Equal(
                ["station.main"],
                contextBody.RootElement.GetProperty("stationSystemIds")
                    .EnumerateArray()
                    .Select(station => station.GetString()
                        ?? throw new InvalidDataException("Frozen Station System ID is null."))
                    .ToArray());

            var liveFlowPath = Assert.Single(Directory.GetFiles(
                Path.Combine(projectDirectory, "applications"),
                "flow.json",
                SearchOption.AllDirectories));
            File.Delete(liveFlowPath);
            var liveProductionLinePath = Assert.Single(Directory.GetFiles(
                Path.Combine(projectDirectory, "applications", applicationId, "production", "lines"),
                "line.json",
                SearchOption.AllDirectories));
            File.Delete(liveProductionLinePath);

            using (var contextAfterDraftDeletionResponse = await _client.GetAsync(contextPath))
            using (var contextAfterDraftDeletionBody = await ReadJsonAsync(contextAfterDraftDeletionResponse))
            {
                Assert.Equal(HttpStatusCode.OK, contextAfterDraftDeletionResponse.StatusCode);
                Assert.Equal(
                    frozenProductModelId,
                    contextAfterDraftDeletionBody.RootElement.GetProperty("productModelId").GetString());
                Assert.Equal(
                    frozenEntryStationSystemId,
                    contextAfterDraftDeletionBody.RootElement.GetProperty("entryStationSystemId").GetString());
            }

            using (var restartedFactory = new StationPackageWebApplicationFactory())
            using (var restartedClient = restartedFactory.CreateClient(
                       new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
            {
                using var openResponse = await restartedClient.PostAsJsonAsync(
                    "/api/automation-project-workspaces/open",
                    new { projectPath = projectDirectory });
                Assert.Equal(HttpStatusCode.OK, openResponse.StatusCode);

                using var restartedContextResponse = await restartedClient.GetAsync(contextPath);
                using var restartedContextBody = await ReadJsonAsync(restartedContextResponse);
                Assert.Equal(HttpStatusCode.OK, restartedContextResponse.StatusCode);
                Assert.Equal(
                    frozenProductModelId,
                    restartedContextBody.RootElement.GetProperty("productModelId").GetString());
                Assert.Equal(
                    frozenIdentityInputKey,
                    restartedContextBody.RootElement
                        .GetProperty("productModelIdentityInputKey")
                        .GetString());
                Assert.Equal(
                    frozenEntryStationSystemId,
                    restartedContextBody.RootElement.GetProperty("entryStationSystemId").GetString());
                Assert.Equal(
                    ["station.main"],
                    restartedContextBody.RootElement.GetProperty("stationSystemIds")
                        .EnumerateArray()
                        .Select(station => station.GetString()
                            ?? throw new InvalidDataException("Frozen Station System ID is null."))
                        .ToArray());
            }

            var productionRunId = Guid.NewGuid();
            var productionUnitId = Guid.NewGuid();
            const string actorId = "api-test";
            using var registerUnitResponse = await _client.PostAsJsonAsync(
                "/api/production-units",
                new
                {
                    productionUnitId,
                    productModelId = frozenProductModelId,
                    identityKey = frozenIdentityInputKey,
                    identityValue = $"UNIT-{suffix}",
                    lotId = (string?)null,
                    actorId,
                    occurredAtUtc = DateTimeOffset.UtcNow
                });
            Assert.Equal(HttpStatusCode.Created, registerUnitResponse.StatusCode);
            using var arriveUnitResponse = await _client.PostAsJsonAsync(
                $"/api/production-units/{productionUnitId:D}/arrivals",
                new
                {
                    lineId = productionLineDefinitionId,
                    stationSystemId = frozenEntryStationSystemId,
                    actorId,
                    occurredAtUtc = DateTimeOffset.UtcNow
                });
            Assert.Equal(HttpStatusCode.OK, arriveUnitResponse.StatusCode);

            using var startResponse = await _client.PostAsJsonAsync(
                "/api/production-runs",
                new
                {
                    projectId,
                    projectSnapshotId = snapshotId,
                    productionRunId = productionRunId.ToString("D"),
                    productionUnitId = productionUnitId.ToString("D"),
                    actorId
                });
            using var startBody = await ReadJsonAsync(startResponse);
            Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
            Assert.Equal(productionRunId, startBody.RootElement.GetProperty("productionRunId").GetGuid());
            Assert.Equal(
                $"/api/production-runs/{productionRunId:D}",
                startResponse.Headers.Location?.OriginalString);
            Assert.Equal(snapshotId, startBody.RootElement.GetProperty("projectSnapshotId").GetString());
            Assert.Equal(projectId, startBody.RootElement.GetProperty("projectId").GetString());
            Assert.Equal(applicationId, startBody.RootElement.GetProperty("applicationId").GetString());
            Assert.Equal(topologyId, startBody.RootElement.GetProperty("topologyId").GetString());
            Assert.Equal(
                productionLineDefinitionId,
                startBody.RootElement.GetProperty("productionLineDefinitionId").GetString());
            Assert.Equal(
                $"UNIT-{suffix}",
                startBody.RootElement.GetProperty("productionUnitIdentity").GetProperty("value").GetString());
            Assert.Equal(actorId, startBody.RootElement.GetProperty("actorId").GetString());
            Assert.Equal(JsonValueKind.Null, startBody.RootElement.GetProperty("lotId").ValueKind);
            Assert.Equal(JsonValueKind.Null, startBody.RootElement.GetProperty("carrierId").ValueKind);
            Assert.Equal("Pending", startBody.RootElement.GetProperty("executionStatus").GetString());
            Assert.False(startBody.RootElement.GetProperty("isTerminal").GetBoolean());

            using var productionRunBody = await WaitForTerminalProductionRunAsync(_client, productionRunId);
            Assert.Equal(productionRunId, productionRunBody.RootElement.GetProperty("productionRunId").GetGuid());
            Assert.Equal("api-test", productionRunBody.RootElement.GetProperty("actorId").GetString());
            Assert.Equal($"UNIT-{suffix}", productionRunBody.RootElement
                .GetProperty("productionUnitIdentity")
                .GetProperty("value")
                .GetString());
            Assert.Equal("Completed", productionRunBody.RootElement.GetProperty("executionStatus").GetString());
            Assert.Equal("NotApplicable", productionRunBody.RootElement.GetProperty("judgement").GetString());
            Assert.Equal("Completed", productionRunBody.RootElement.GetProperty("disposition").GetString());
            Assert.True(productionRunBody.RootElement.GetProperty("isTerminal").GetBoolean());
            Assert.Equal(
                productionRunBody.RootElement.GetProperty("completedAtUtc").GetDateTimeOffset(),
                productionRunBody.RootElement.GetProperty("lastTransitionAtUtc").GetDateTimeOffset());
            var operation = Assert.Single(productionRunBody.RootElement.GetProperty("operations").EnumerateArray());
            Assert.Equal("operation.main", operation.GetProperty("operationId").GetString());
            Assert.Equal("Completed", operation.GetProperty("executionStatus").GetString());
            Assert.Equal(processDefinitionId, operation.GetProperty("processDefinitionId").GetString());
            Assert.Equal(processVersionId, operation.GetProperty("processVersionId").GetString());
            Assert.Equal(configurationSnapshotId, operation.GetProperty("configurationSnapshotId").GetString());
            var sessionId = operation.GetProperty("runtimeSessionId").GetGuid();

            using var sessionResponse = await _client.GetAsync($"/api/runtime/sessions/{sessionId}");
            using var sessionBody = await ReadJsonAsync(sessionResponse);
            Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
            Assert.Equal(processDefinitionId, sessionBody.RootElement.GetProperty("processDefinitionId").GetString());
            Assert.Equal(processVersionId, sessionBody.RootElement.GetProperty("processVersionId").GetString());
            Assert.Equal(configurationSnapshotId, sessionBody.RootElement.GetProperty("configurationSnapshotId").GetString());
            Assert.Equal(snapshotId, sessionBody.RootElement.GetProperty("projectSnapshotId").GetString());
            Assert.Equal(productionRunId, sessionBody.RootElement.GetProperty("productionRunId").GetGuid());
            Assert.Equal(productionLineDefinitionId, sessionBody.RootElement
                .GetProperty("productionLineDefinitionId")
                .GetString());
            Assert.Equal(operation.GetProperty("operationId").GetString(), sessionBody.RootElement
                .GetProperty("operationId")
                .GetString());
            Assert.Equal(operation.GetProperty("attempt").GetInt32(), sessionBody.RootElement
                .GetProperty("operationAttempt")
                .GetInt32());
            Assert.Equal(operation.GetProperty("stationSystemId").GetString(), sessionBody.RootElement
                .GetProperty("stationSystemId")
                .GetString());
            Assert.Equal("api-test", sessionBody.RootElement.GetProperty("actorId").GetString());
            Assert.Equal($"UNIT-{suffix}", sessionBody.RootElement
                .GetProperty("productionUnitIdentity")
                .GetProperty("value")
                .GetString());
            Assert.False(sessionBody.RootElement.TryGetProperty("serialNumber", out _));

            using var traceBody = await WaitForEngineeringTraceAsync(_client, snapshotId);
            var traceRow = Assert.Single(traceBody.RootElement.GetProperty("results").GetProperty("items").EnumerateArray());
            Assert.Equal(productionRunId, traceRow.GetProperty("productionRunId").GetGuid());
            Assert.Equal(snapshotId, traceRow.GetProperty("projectSnapshotId").GetString());
        }
        finally
        {
            DeleteProjectDirectory(projectDirectory);
        }
    }

    [Fact]
    public async Task ProductionRunContextRejectsColdStartedManifestHashAndIdentityTampering()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-context-tamper-{suffix}";
        var applicationId = $"application-context-tamper-{suffix}";
        var topologyId = $"topology-context-tamper-{suffix}";
        var layoutId = $"layout-context-tamper-{suffix}";
        var processDefinitionId = $"process-context-tamper-{suffix}";
        var processVersionId = $"{processDefinitionId}@1.0.0";
        var configurationSnapshotId = $"configuration-context-tamper-{suffix}";
        var productionLineDefinitionId = $"line-scoped-{suffix}";
        var snapshotId = $"snapshot-context-tamper-{suffix}";
        var sourceDirectory = ProjectReleaseTestDirectory($"context-source-{suffix}");
        var tamperedDirectories = new List<string>();

        try
        {
            await CreateScopedReleaseSourceAsync(
                sourceDirectory,
                projectId,
                applicationId,
                topologyId,
                layoutId,
                processDefinitionId,
                processVersionId,
                configurationSnapshotId,
                $"capability-context-tamper-{suffix}",
                $"binding-context-tamper-{suffix}",
                suffix);
            using (var publishResponse = await _client.PostAsJsonAsync(
                       $"/api/automation-projects/{projectId}/snapshots",
                       new
                       {
                           snapshotId,
                           applicationId,
                           productionLineDefinitionId
                       }))
            {
                Assert.Equal(HttpStatusCode.Created, publishResponse.StatusCode);
            }

            foreach (var tamper in new[] { "manifest", "hash", "identity" })
            {
                var tamperedDirectory = ProjectReleaseTestDirectory($"context-{tamper}-{suffix}");
                tamperedDirectories.Add(tamperedDirectory);
                CopyDirectory(sourceDirectory, tamperedDirectory);
                var projectFilePath = Assert.Single(Directory.GetFiles(
                    tamperedDirectory,
                    "*.oloproj",
                    SearchOption.TopDirectoryOnly));
                var projectManifest = JsonNode.Parse(
                    await File.ReadAllTextAsync(projectFilePath))!.AsObject();
                var projectSnapshot = Assert.IsType<JsonObject>(
                    Assert.Single(projectManifest["snapshots"]!.AsArray()));
                var releaseManifestPath = Path.Combine(
                    tamperedDirectory,
                    projectSnapshot["releaseManifestPath"]!.GetValue<string>()
                        .Replace('/', Path.DirectorySeparatorChar));
                var releaseManifest = JsonNode.Parse(
                    await File.ReadAllTextAsync(releaseManifestPath))!.AsObject();
                switch (tamper)
                {
                    case "manifest":
                        releaseManifest["unexpectedManifestField"] = true;
                        await File.WriteAllTextAsync(
                            releaseManifestPath,
                            releaseManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        break;
                    case "hash":
                        var frozenFile = Assert.IsType<JsonObject>(
                            Assert.Single(releaseManifest["files"]!.AsArray().Take(1)));
                        var frozenFilePath = Path.Combine(
                            Path.GetDirectoryName(releaseManifestPath)!,
                            "source",
                            frozenFile["relativePath"]!.GetValue<string>()
                                .Replace('/', Path.DirectorySeparatorChar));
                        var frozenFileBytes = await File.ReadAllBytesAsync(frozenFilePath);
                        Assert.NotEmpty(frozenFileBytes);
                        frozenFileBytes[0] ^= 0x01;
                        await File.WriteAllBytesAsync(frozenFilePath, frozenFileBytes);
                        break;
                    case "identity":
                        releaseManifest["projectId"] = $"{projectId}.tampered";
                        await File.WriteAllTextAsync(
                            releaseManifestPath,
                            releaseManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported tamper case {tamper}.");
                }

                using var restartedFactory = new StationPackageWebApplicationFactory();
                using var restartedClient = restartedFactory.CreateClient(
                    new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                using var openResponse = await restartedClient.PostAsJsonAsync(
                    "/api/automation-project-workspaces/open",
                    new { projectPath = tamperedDirectory });
                Assert.Equal(HttpStatusCode.OK, openResponse.StatusCode);

                using var contextResponse = await restartedClient.GetAsync(
                    $"/api/automation-projects/{projectId}/snapshots/{snapshotId}/production-run-context");
                using var contextBody = await ReadJsonAsync(contextResponse);
                Assert.Equal(HttpStatusCode.Conflict, contextResponse.StatusCode);
                Assert.Equal(
                    "Conflict.Projects.ProjectReleaseInvalid",
                    contextBody.RootElement.GetProperty("title").GetString());
                Assert.Contains(
                    tamper switch
                    {
                        "manifest" => "invalid JSON",
                        "hash" => "SHA-256 does not match",
                        "identity" => "scope is",
                        _ => throw new InvalidOperationException($"Unsupported tamper case {tamper}.")
                    },
                    contextBody.RootElement.GetProperty("detail").GetString(),
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteProjectDirectory(sourceDirectory);
            foreach (var directory in tamperedDirectories)
            {
                DeleteProjectDirectory(directory);
            }
        }
    }

    [Fact]
    public async Task ProjectSnapshotPublicationRejectsMissingProductionLine()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-missing-production-{suffix}";
        var applicationId = $"application-missing-production-{suffix}";
        var processDefinitionId = $"process-missing-production-{suffix}";
        var configurationSnapshotId = $"configuration-missing-production-{suffix}";
        var projectDirectory = ProjectReleaseTestDirectory(suffix);

        try
        {
            await CreateScopedReleaseSourceAsync(
                projectDirectory,
                projectId,
                applicationId,
                $"topology-missing-production-{suffix}",
                $"layout-missing-production-{suffix}",
                processDefinitionId,
                $"{processDefinitionId}@1.0.0",
                configurationSnapshotId,
                $"capability-missing-production-{suffix}",
                $"binding-missing-production-{suffix}",
                suffix,
                createProductionLine: false);

            using var response = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/snapshots",
                new
                {
                    snapshotId = $"snapshot-missing-production-{suffix}",
                    applicationId,
                    productionLineDefinitionId = $"line-missing-{suffix}"
                });
            using var body = await ReadJsonAsync(response);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(
                "NotFound.Projects.ReleaseProductionLineNotFound",
                body.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            DeleteProjectDirectory(projectDirectory);
        }
    }

    [Fact]
    public async Task ProjectSnapshotPublicationRejectsRemovedTopLevelRuntimeSelectionFields()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/automation-projects/project.old-publication-shape/snapshots",
            new
            {
                snapshotId = "snapshot.old-publication-shape",
                applicationId = "application.old-publication-shape",
                productionLineDefinitionId = "line.old-publication-shape",
                processDefinitionId = "process.removed",
                processVersionId = "process.removed@1",
                configurationSnapshotId = "configuration.removed"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProjectSnapshotPublicationFreezesRepeatedFlowOperationsWithIndependentConfigurations()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-repeated-flow-{suffix}";
        var applicationId = $"application-repeated-flow-{suffix}";
        var topologyId = $"topology-repeated-flow-{suffix}";
        var processDefinitionId = $"process-repeated-flow-{suffix}";
        var processVersionId = $"{processDefinitionId}@1.0.0";
        var firstConfigurationId = $"configuration-repeated-flow-a-{suffix}";
        var secondConfigurationId = $"configuration-repeated-flow-b-{suffix}";
        var productionLineDefinitionId = $"line-scoped-{suffix}";
        var projectDirectory = ProjectReleaseTestDirectory(suffix);

        try
        {
            await CreateScopedReleaseSourceAsync(
                projectDirectory,
                projectId,
                applicationId,
                topologyId,
                $"layout-repeated-flow-{suffix}",
                processDefinitionId,
                processVersionId,
                firstConfigurationId,
                $"capability-repeated-flow-{suffix}",
                $"binding-repeated-flow-{suffix}",
                suffix);

            var engineeringBase =
                $"/api/automation-projects/{projectId}/applications/{applicationId}/engineering";
            using (var secondConfigurationResponse = await _client.PostAsJsonAsync(
                       $"{engineeringBase}/projects/engineering-{suffix}/configuration-snapshots",
                       new
                       {
                           snapshotId = secondConfigurationId,
                           processDefinitionId,
                           processVersionId,
                           recipeId = $"recipe-{suffix}",
                           stationProfileId = $"station-{suffix}"
                       }))
            {
                Assert.Equal(HttpStatusCode.Created, secondConfigurationResponse.StatusCode);
            }

            var repeatedLinePath =
                $"/api/automation-projects/{projectId}/applications/{applicationId}/production-lines/{productionLineDefinitionId}";
            using (var replaceLineResponse = await _client.PutEditorAsync(
                       repeatedLinePath,
                       repeatedLinePath,
                       new
                       {
                           lineDefinitionId = productionLineDefinitionId,
                           displayName = "Repeated Flow Line",
                           topologyId,
                            productModel = new
                            {
                                productModelId = $"product-scoped-{suffix}",
                                modelCode = $"MODEL-{suffix}",
                                identityInputKey = "serialNumber"
                            },
                            entryOperationId = "operation.first",
                            operations = new[]
                            {
                                new
                                {
                                    operationId = "operation.first",
                                    displayName = "First",
                                    stationSystemId = "station.main",
                                    flowDefinitionId = processDefinitionId,
                                    configurationSnapshotId = firstConfigurationId,
                                    resources = StationResources("first")
                                },
                                new
                                {
                                    operationId = "operation.second",
                                    displayName = "Second",
                                    stationSystemId = "station.main",
                                    flowDefinitionId = processDefinitionId,
                                    configurationSnapshotId = secondConfigurationId,
                                    resources = StationResources("second")
                                }
                            },
                            transitions = new[]
                            {
                                new
                                {
                                    transitionId = "transition.first-to-second",
                                    sourceOperationId = "operation.first",
                                    targetOperationId = "operation.second",
                                    kind = "Sequence"
                                }
                            },
                            lineControllerAuthorizations = Array.Empty<object>()
                       }))
            {
                Assert.Equal(HttpStatusCode.OK, replaceLineResponse.StatusCode);
            }

            using var publishResponse = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/snapshots",
                new
                {
                    snapshotId = $"snapshot-repeated-flow-{suffix}",
                    applicationId,
                    productionLineDefinitionId
                });
            using var publishBody = await ReadJsonAsync(publishResponse);
            Assert.Equal(HttpStatusCode.Created, publishResponse.StatusCode);
            var snapshot = Assert.Single(publishBody.RootElement.GetProperty("snapshots").EnumerateArray());
            var manifestPath = Path.Combine(
                projectDirectory,
                snapshot.GetProperty("releaseManifestPath").GetString()!
                    .Replace('/', Path.DirectorySeparatorChar));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var operations = manifest.RootElement
                .GetProperty("metadata")
                .GetProperty("productionLine")
                .GetProperty("operations")
                .EnumerateArray()
                .ToArray();

            Assert.Equal(2, operations.Length);
            Assert.All(operations, operation => Assert.Equal(
                processDefinitionId,
                operation.GetProperty("flowDefinitionId").GetString()));
            Assert.Equal(
                [firstConfigurationId, secondConfigurationId],
                operations.Select(operation => operation.GetProperty("configurationSnapshotId").GetString()));
        }
        finally
        {
            DeleteProjectDirectory(projectDirectory);
        }
    }

    [Fact]
    public async Task ProjectSnapshotPublicationRejectsStaleProductionStationSystemReference()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-stale-production-{suffix}";
        var applicationId = $"application-stale-production-{suffix}";
        var processDefinitionId = $"process-stale-production-{suffix}";
        var configurationSnapshotId = $"configuration-stale-production-{suffix}";
        var productionLineDefinitionId = $"line-scoped-{suffix}";
        var projectDirectory = ProjectReleaseTestDirectory(suffix);

        try
        {
            await CreateScopedReleaseSourceAsync(
                projectDirectory,
                projectId,
                applicationId,
                $"topology-stale-production-{suffix}",
                $"layout-stale-production-{suffix}",
                processDefinitionId,
                $"{processDefinitionId}@1.0.0",
                configurationSnapshotId,
                $"capability-stale-production-{suffix}",
                $"binding-stale-production-{suffix}",
                suffix);

            var linePath = Assert.Single(Directory.GetFiles(
                projectDirectory,
                "line.json",
                SearchOption.AllDirectories));
            var line = JsonNode.Parse(await File.ReadAllTextAsync(linePath))!.AsObject();
            var operation = line["operations"]!.AsArray()[0]!.AsObject();
            operation["stationSystemId"] = "station.stale";
            operation["resources"]!.AsArray()
                .Single(resource => string.Equals(
                    resource!["kind"]!.GetValue<string>(),
                    "Station",
                    StringComparison.Ordinal))!["topologyTargetId"] = "station.stale";
            await File.WriteAllTextAsync(linePath, line.ToJsonString());

            using var response = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/snapshots",
                new
                {
                    snapshotId = $"snapshot-stale-production-{suffix}",
                    applicationId,
                    productionLineDefinitionId
                });
            using var body = await ReadJsonAsync(response);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(
                "Conflict.Projects.ReleaseProductionOperationStationSystemInvalid",
                body.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            DeleteProjectDirectory(projectDirectory);
        }
    }

    [Fact]
    public async Task SiteLayoutRejectsMissingTopologyTarget()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-layout-missing-target-{suffix}";
        var applicationId = $"application-layout-missing-target-{suffix}";
        var topologyId = $"topology-layout-missing-target-{suffix}";
        var layoutId = $"layout-missing-target-{suffix}";
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-api-layout-validation-tests",
            suffix);

        try
        {
            using var createWorkspaceResponse = await _client.PostAsJsonAsync(
                "/api/automation-project-workspaces",
                new
                {
                    projectId,
                    displayName = "Layout Validation Project",
                    projectPath = projectDirectory,
                    defaultApplicationId = applicationId,
                    defaultApplicationName = "Layout Validation Application"
                });
            Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);

            var topologiesPath = ProjectTopologiesPath(projectId, applicationId);
            using var createTopologyResponse = await _client.PostAsJsonAsync(
                topologiesPath,
                new
                {
                    topologyId,
                    displayName = "Layout Validation Topology"
                });
            Assert.Equal(HttpStatusCode.Created, createTopologyResponse.StatusCode);

            var layoutsPath = ProjectLayoutsPath(projectId, applicationId);
            using var createLayoutResponse = await _client.PostAsJsonAsync(
                layoutsPath,
                new
                {
                    layoutId,
                    topologyId,
                    displayName = "Layout",
                    canvasWidth = 800,
                    canvasHeight = 600,
                    units = "mm"
                });
            Assert.Equal(HttpStatusCode.Created, createLayoutResponse.StatusCode);

            using var addMissingTargetResponse = await _client.PostEditorAsync(
                $"{layoutsPath}/{layoutId}",
                $"{layoutsPath}/{layoutId}/elements",
                new
                {
                    elementId = $"layout-missing-system-{suffix}",
                    kind = "SystemShape",
                    target = new
                    {
                        kind = "System",
                        targetId = $"missing-system-{suffix}"
                    },
                    parentElementId = (string?)null,
                    x = 10,
                    y = 20,
                    width = 160,
                    height = 120,
                    rotationDegrees = 0,
                    zIndex = 1,
                    style = new Dictionary<string, string>()
                });
            using var body = await ReadJsonAsync(addMissingTargetResponse);

            Assert.Equal(HttpStatusCode.BadRequest, addMissingTargetResponse.StatusCode);
            Assert.Equal(
                "Validation.Topology.LayoutTargetMissing",
                body.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            DeleteProjectDirectory(projectDirectory);
        }
    }

    [Fact]
    public async Task DuplicateAutomationProjectReturnsConflict()
    {
        var projectId = $"project-duplicate-{Guid.NewGuid():N}";
        var request = new
        {
            projectId,
            displayName = "Duplicate Project",
            projectPath = $"C:/OpenLineOps/Projects/{projectId}"
        };

        using var firstResponse = await _client.PostAsJsonAsync("/api/automation-projects", request);
        using var secondResponse = await _client.PostAsJsonAsync("/api/automation-projects", request);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
    }

    private async Task CreateScopedReleaseSourceAsync(
        string projectDirectory,
        string projectId,
        string applicationId,
        string topologyId,
        string layoutId,
        string processDefinitionId,
        string processVersionId,
        string configurationSnapshotId,
        string capabilityId,
        string bindingId,
        string suffix,
        bool createProductionLine = true)
    {
        using var createWorkspaceResponse = await _client.PostAsJsonAsync(
            "/api/automation-project-workspaces",
            new
            {
                projectId,
                displayName = $"Scoped Release {suffix}",
                projectPath = projectDirectory,
                defaultApplicationId = applicationId,
                defaultApplicationName = "Main Application"
            });
        Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);

        var topologiesPath = ProjectTopologiesPath(projectId, applicationId);
        using var createTopologyResponse = await _client.PostAsJsonAsync(
            topologiesPath,
            new { topologyId, displayName = "Scoped Release Topology" });
        Assert.Equal(HttpStatusCode.Created, createTopologyResponse.StatusCode);
        using var capabilityResponse = await _client.PostEditorAsync(
            $"{topologiesPath}/{topologyId}",
            $"{topologiesPath}/{topologyId}/capabilities",
            new
            {
                capabilityId,
                commandName = "Inspect",
                version = "1.0.0",
                inputSchema = """{"type":"object"}""",
                outputSchema = (string?)null,
                timeoutSeconds = 30,
                safetyClass = "Normal"
            });
        Assert.Equal(HttpStatusCode.OK, capabilityResponse.StatusCode);
        await AddProjectNodeAsync(
            topologiesPath,
            topologyId,
            "station.main",
            null,
            "Station",
            "Main Station",
            [capabilityId]);
        using var bindingResponse = await _client.PostEditorAsync(
            $"{topologiesPath}/{topologyId}",
            $"{topologiesPath}/{topologyId}/driver-bindings",
            new
            {
                bindingId,
                ownerSystemId = "station.main",
                capabilityId,
                providerKind = "Simulator",
                providerKey = $"simulator.{applicationId}"
            });
        Assert.Equal(HttpStatusCode.OK, bindingResponse.StatusCode);

        var layoutsPath = ProjectLayoutsPath(projectId, applicationId);
        using var createLayoutResponse = await _client.PostAsJsonAsync(
            layoutsPath,
            new
            {
                layoutId,
                topologyId,
                displayName = "Scoped Release Layout",
                canvasWidth = 800,
                canvasHeight = 600,
                units = "mm"
            });
        Assert.Equal(HttpStatusCode.Created, createLayoutResponse.StatusCode);
        using var addLayoutElementResponse = await _client.PostEditorAsync(
            $"{layoutsPath}/{layoutId}",
            $"{layoutsPath}/{layoutId}/elements",
            new
            {
                elementId = "element.station",
                kind = "SystemShape",
                target = new { kind = "System", targetId = "station.main" },
                parentElementId = (string?)null,
                x = 20,
                y = 30,
                width = 100,
                height = 80,
                rotationDegrees = 0,
                zIndex = 1,
                style = new Dictionary<string, string>()
            });
        Assert.Equal(HttpStatusCode.OK, addLayoutElementResponse.StatusCode);

        var processesPath = ProjectProcessesPath(projectId, applicationId);
        using var createProcessResponse = await _client.PostAsJsonAsync(
            processesPath,
            CreateRuntimeProcessDefinitionRequest(processDefinitionId, processVersionId, capabilityId));
        Assert.Equal(HttpStatusCode.Created, createProcessResponse.StatusCode);
        using var publishProcessResponse = await _client.PostEditorAsync<object?>(
            $"{processesPath}/{processDefinitionId}",
            $"{processesPath}/{processDefinitionId}/publish",
            null);
        Assert.Equal(HttpStatusCode.OK, publishProcessResponse.StatusCode);

        await CreateScopedPublishedEngineeringSnapshotAsync(
            projectId,
            applicationId,
            $"recipe-{suffix}",
            $"station-{suffix}",
            $"engineering-{suffix}",
            configurationSnapshotId,
            processDefinitionId,
            processVersionId,
            capabilityId);

        if (createProductionLine)
        {
            var productionLineDefinitionId = $"line-scoped-{suffix}";
            using var createProductionLineResponse = await _client.PostAsJsonAsync(
                $"/api/automation-projects/{projectId}/applications/{applicationId}/production-lines",
                new
                {
                    lineDefinitionId = productionLineDefinitionId,
                    displayName = "Scoped Release Production Line",
                    topologyId,
                    productModel = new
                    {
                        productModelId = $"product-scoped-{suffix}",
                        modelCode = $"MODEL-{suffix}",
                        identityInputKey = "serialNumber"
                    },
                    entryOperationId = "operation.main",
                    operations = new[]
                    {
                        new
                        {
                            operationId = "operation.main",
                            displayName = "Main Operation",
                            stationSystemId = "station.main",
                            flowDefinitionId = processDefinitionId,
                            configurationSnapshotId,
                            resources = StationResources("main")
                        }
                    },
                    transitions = Array.Empty<object>(),
                    lineControllerAuthorizations = Array.Empty<object>()
                });
            Assert.Equal(HttpStatusCode.Created, createProductionLineResponse.StatusCode);
        }

        using var linkTopologyResponse = await _client.PutAsJsonAsync(
            $"/api/automation-projects/{projectId}/applications/{applicationId}/topology",
            new { topologyId });
        Assert.Equal(HttpStatusCode.OK, linkTopologyResponse.StatusCode);
        using var linkProcessResponse = await _client.PutAsync(
            $"/api/automation-projects/{projectId}/applications/{applicationId}/process-definitions/{processDefinitionId}",
            content: null);
        Assert.Equal(HttpStatusCode.OK, linkProcessResponse.StatusCode);
    }

    private static object[] StationResources(string suffix) =>
        [new
        {
            bindingId = $"resource.station.{suffix}",
            kind = "Station",
            topologyTargetId = "station.main",
            resolution = "Fixed"
        }];

    private async Task CreateScopedPublishedEngineeringSnapshotAsync(
        string projectId,
        string applicationId,
        string recipeId,
        string stationProfileId,
        string engineeringProjectId,
        string configurationSnapshotId,
        string processDefinitionId,
        string processVersionId,
        string capabilityId)
    {
        var engineeringBase = $"/api/automation-projects/{projectId}/applications/{applicationId}/engineering";
        var workspaceId = $"workspace-{engineeringProjectId}";
        using var createWorkspaceResponse = await _client.PostAsJsonAsync(
            $"{engineeringBase}/workspaces",
            new { workspaceId, displayName = "Release Workspace" });
        using var createRecipeResponse = await _client.PostAsJsonAsync(
            $"{engineeringBase}/recipes",
            new
            {
                recipeId,
                versionId = $"{recipeId}@1.0.0",
                displayName = "Release Recipe",
                parameters = new[] { new { key = "scan.mode", value = "release" } }
            });
        using var publishRecipeResponse = await _client.PostAsync(
            $"{engineeringBase}/recipes/{recipeId}/publish",
            content: null);
        using var createStationResponse = await _client.PostAsJsonAsync(
            $"{engineeringBase}/station-profiles",
            new
            {
                stationProfileId,
                stationSystemId = "station.main",
                displayName = "Release Station",
                deviceBindings = new[]
                {
                    new
                    {
                        deviceBindingId = "binding.primary",
                        ownerSystemId = "station.main",
                        capabilityId,
                        deviceKey = "scanner-01"
                    }
                }
            });
        using var createProjectResponse = await _client.PostAsJsonAsync(
            $"{engineeringBase}/projects",
            new
            {
                projectId = engineeringProjectId,
                workspaceId,
                displayName = "Release Engineering Project"
            });
        using var publishSnapshotResponse = await _client.PostAsJsonAsync(
            $"{engineeringBase}/projects/{engineeringProjectId}/configuration-snapshots",
            new
            {
                snapshotId = configurationSnapshotId,
                processDefinitionId,
                processVersionId,
                recipeId,
                stationProfileId
            });

        Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createStationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createProjectResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, publishSnapshotResponse.StatusCode);
    }

    private async Task AddProjectNodeAsync(
        string topologiesPath,
        string topologyId,
        string nodeId,
        string? parentNodeId,
        string kind,
        string displayName,
        IReadOnlyCollection<string>? providedCapabilityIds = null)
    {
        using var response = await _client.PostEditorAsync(
            $"{topologiesPath}/{topologyId}",
            $"{topologiesPath}/{topologyId}/systems",
            new
            {
                systemId = nodeId,
                parentSystemId = parentNodeId,
                kind,
                systemType = "test.system",
                displayName,
                requiredCapabilityIds = Array.Empty<string>(),
                providedCapabilityIds = providedCapabilityIds ?? Array.Empty<string>(),
                metadata = new Dictionary<string, string>()
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static void AssertImmutableRelease(JsonElement snapshot, string projectDirectory)
    {
        var relativeManifestPath = snapshot.GetProperty("releaseManifestPath").GetString();
        var contentSha256 = snapshot.GetProperty("releaseContentSha256").GetString();
        Assert.False(string.IsNullOrWhiteSpace(relativeManifestPath));
        Assert.False(Path.IsPathRooted(relativeManifestPath));
        Assert.DoesNotContain("..", relativeManifestPath, StringComparison.Ordinal);
        Assert.NotNull(contentSha256);
        Assert.Equal(64, contentSha256.Length);
        Assert.All(contentSha256, character => Assert.True(Uri.IsHexDigit(character)));

        var manifestPath = Path.GetFullPath(Path.Combine(
            projectDirectory,
            relativeManifestPath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.StartsWith(
            Path.GetFullPath(projectDirectory) + Path.DirectorySeparatorChar,
            manifestPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(manifestPath), $"Release manifest was not found at {manifestPath}.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal("openlineops.project-release-artifact", manifest.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(contentSha256, manifest.RootElement.GetProperty("contentSha256").GetString());
    }

    private static string ProjectReleaseTestDirectory(string suffix)
    {
        return Path.Combine(Path.GetTempPath(), "openlineops-api-project-release-tests", suffix);
    }

    private static void DeleteProjectDirectory(string projectDirectory)
    {
        if (Directory.Exists(projectDirectory))
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    private static string[] ApplicationFileInventory(string applicationRoot) =>
        Directory.EnumerateFiles(applicationRoot, "*", SearchOption.AllDirectories)
            .Select(path =>
                $"{Path.GetRelativePath(applicationRoot, path).Replace('\\', '/')}:"
                + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static JsonDocument ReadPackageManifest(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry("package.manifest.json")
            ?? throw new InvalidDataException($"Package '{packagePath}' has no manifest.");
        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static bool PackageBelongsTo(
        string packagePath,
        string projectId,
        string applicationId)
    {
        using var manifest = ReadPackageManifest(packagePath);
        return manifest.RootElement.GetProperty("projectId").GetString() == projectId
            && manifest.RootElement.GetProperty("applicationId").GetString() == applicationId;
    }

    private static string? ReadPackageSignatureAlgorithm(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry("package.signature.json")
            ?? throw new InvalidDataException($"Package '{packagePath}' has no signature.");
        using var stream = entry.Open();
        using var signature = JsonDocument.Parse(stream);
        return signature.RootElement.GetProperty("algorithm").GetString();
    }

    private static string ProjectTopologiesPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/topologies";
    }

    private static string ProjectLayoutsPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/layouts";
    }

    private static string ProjectProcessesPath(string projectId, string applicationId)
    {
        return $"/api/automation-projects/{projectId}/applications/{applicationId}/processes";
    }

    private static object CreateRuntimeProcessDefinitionRequest(
        string processDefinitionId,
        string processVersionId = "packaging-line-eol@1.0.0",
        string capabilityId = "device.scanner")
    {
        return new
        {
            processDefinitionId,
            versionId = processVersionId,
            displayName = "Runtime Snapshot Process",
            nodes = new[]
            {
                new
                {
                    nodeId = "start",
                    kind = "Start",
                    displayName = "Start",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    targetKind = (string?)null,
                    targetId = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                },
                new
                {
                    nodeId = "inspect",
                    kind = "Command",
                    displayName = "Inspect",
                    requiredCapability = (string?)capabilityId,
                    commandName = (string?)"Inspect",
                    targetKind = (string?)"Capability",
                    targetId = (string?)capabilityId,
                    timeoutSeconds = (int?)30,
                    inputPayload = (string?)"""{"scan":"ok"}"""
                },
                new
                {
                    nodeId = "end",
                    kind = "End",
                    displayName = "End",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    targetKind = (string?)null,
                    targetId = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                }
            },
            transitions = new[]
            {
                new
                {
                    transitionId = "start-to-inspect",
                    fromNodeId = "start",
                    toNodeId = "inspect",
                    label = (string?)null
                },
                new
                {
                    transitionId = "inspect-to-end",
                    fromNodeId = "inspect",
                    toNodeId = "end",
                    label = (string?)"ok"
                }
            }
        };
    }

    private static async Task<JsonDocument> WaitForTerminalProductionRunAsync(
        HttpClient client,
        Guid productionRunId)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            using var response = await client.GetAsync($"/api/production-runs/{productionRunId:D}");
            var document = await ReadJsonAsync(response);
            if (response.StatusCode == HttpStatusCode.OK
                && document.RootElement.GetProperty("isTerminal").GetBoolean())
            {
                return document;
            }

            document.Dispose();
            await Task.Delay(50);
        }

        throw new TimeoutException($"Production Run {productionRunId:D} did not reach a terminal state.");
    }

    private static async Task<JsonDocument> WaitForEngineeringTraceAsync(
        HttpClient client,
        string projectSnapshotId)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(20);
        var requestPath =
            $"/api/traceability/read-models/engineering-search?projectSnapshotId={Uri.EscapeDataString(projectSnapshotId)}";

        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            using var response = await client.GetAsync(requestPath);
            var document = await ReadJsonAsync(response);
            if (response.StatusCode == HttpStatusCode.OK
                && document.RootElement.GetProperty("results").GetProperty("items").GetArrayLength() > 0)
            {
                return document;
            }

            document.Dispose();
            await Task.Delay(50);
        }

        throw new TimeoutException($"Trace projection for Project Snapshot '{projectSnapshotId}' was not available.");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
