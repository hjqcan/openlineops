extern alias api;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Runner;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerPublishedProjectProcessE2ETests
{
    [Fact]
    public async Task PublishedProjectRunsInASeparateProcessAndInvalidReleasesFailClosed()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var productionRunId = Guid.NewGuid();
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "openlineops-runner-process-e2e",
            suffix);
        var publishedProjectPath = Path.Combine(testRoot, "published");
        var missingReleaseProjectPath = Path.Combine(testRoot, "missing-release");
        var tamperedReleaseProjectPath = Path.Combine(testRoot, "tampered-release");
        var draftProjectPath = Path.Combine(testRoot, "draft-only");

        try
        {
            using var factory = new WebApplicationFactory<api::Program>();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            var published = await PublishRunnableProjectAsync(
                client,
                publishedProjectPath,
                suffix);
            await WriteRunnerConfigurationAsync(publishedProjectPath);

            CopyDirectory(publishedProjectPath, missingReleaseProjectPath);
            CopyDirectory(publishedProjectPath, tamperedReleaseProjectPath);

            var liveFlowPath = Assert.Single(Directory.GetFiles(
                Path.Combine(publishedProjectPath, "applications"),
                "flow.json",
                SearchOption.AllDirectories));
            File.Delete(liveFlowPath);

            var success = await RunRunnerProcessAsync(
                published.ProjectFilePath,
                publishedProjectPath,
                "--snapshot",
                "active",
                "--dut",
                $"DUT-{suffix}",
                "--batch",
                $"BATCH-{suffix}",
                "--fixture",
                $"FIXTURE-{suffix}",
                "--device",
                $"DEVICE-{suffix}",
                "--actor",
                "runner-process-e2e",
                "--run-id",
                productionRunId.ToString("D"));

            Assert.True(
                success.ExitCode == RunnerExitCodes.Success,
                $"Runner exited with {success.ExitCode}. stdout: {success.StandardOutput}; stderr: {success.StandardError}");
            using (var output = ParseSingleJsonOutput(success))
            {
                var root = output.RootElement;
                Assert.True(root.GetProperty("success").GetBoolean());
                Assert.Equal(RunnerExitCodes.Success, root.GetProperty("exitCode").GetInt32());

                var project = root.GetProperty("project");
                Assert.Equal(published.ProjectId, project.GetProperty("projectId").GetString());
                Assert.Equal(published.ApplicationId, project.GetProperty("applicationId").GetString());
                Assert.Equal(published.SnapshotId, project.GetProperty("snapshotId").GetString());
                Assert.Equal(
                    published.ReleaseContentSha256,
                    project.GetProperty("releaseContentSha256").GetString());

                var productionRun = root.GetProperty("productionRun");
                Assert.Equal(
                    productionRunId,
                    productionRun.GetProperty("productionRunId").GetGuid());
                Assert.Equal("Completed", productionRun.GetProperty("status").GetString());
                Assert.Equal(1, productionRun.GetProperty("stageCount").GetInt32());
                Assert.Equal(1, productionRun.GetProperty("completedStageCount").GetInt32());
                Assert.Equal(0, productionRun.GetProperty("incidentCount").GetInt32());
                Assert.True(productionRun.GetProperty("completedStepCount").GetInt32() > 0);
                Assert.True(productionRun.GetProperty("commandCount").GetInt32() > 0);
            }

            var executionDataPath = ProjectExecutionDataDirectory.ForProjectDirectory(
                publishedProjectPath);
            var runtimeDatabasePath = Path.Combine(
                executionDataPath,
                "openlineops-runtime.sqlite");
            var traceDatabasePath = Path.Combine(
                executionDataPath,
                "openlineops-traceability.sqlite");
            Assert.Equal(
                1,
                await CountRunRowsAsync(runtimeDatabasePath, "production_runs", productionRunId));
            Assert.Equal(
                1,
                await CountRunRowsAsync(traceDatabasePath, "trace_records", productionRunId));

            var idempotentRetry = await RunRunnerProcessAsync(
                published.ProjectFilePath,
                publishedProjectPath,
                "--snapshot",
                "active",
                "--dut",
                $"DUT-{suffix}",
                "--batch",
                $"BATCH-{suffix}",
                "--fixture",
                $"FIXTURE-{suffix}",
                "--device",
                $"DEVICE-{suffix}",
                "--actor",
                "runner-process-e2e",
                "--run-id",
                productionRunId.ToString("D"));
            Assert.True(
                idempotentRetry.ExitCode == RunnerExitCodes.Success,
                $"Idempotent retry exited with {idempotentRetry.ExitCode}. "
                + $"stdout: {idempotentRetry.StandardOutput}; stderr: {idempotentRetry.StandardError}");
            Assert.Equal(
                1,
                await CountRunRowsAsync(runtimeDatabasePath, "production_runs", productionRunId));
            Assert.Equal(
                1,
                await CountRunRowsAsync(traceDatabasePath, "trace_records", productionRunId));

            Directory.Delete(
                Path.Combine(missingReleaseProjectPath, "releases"),
                recursive: true);
            var missingRelease = await RunRunnerProcessAsync(
                GetProjectFilePath(missingReleaseProjectPath),
                missingReleaseProjectPath,
                "--snapshot",
                "active",
                "--dut",
                $"DUT-{suffix}",
                "--actor",
                "runner-process-e2e");
            AssertRunnerFailure(
                missingRelease,
                RunnerExitCodes.ImmutableReleaseMissing,
                "NotFound.Projects.ProjectReleaseNotFound");

            TamperReleasedFlow(tamperedReleaseProjectPath);
            var tamperedRelease = await RunRunnerProcessAsync(
                GetProjectFilePath(tamperedReleaseProjectPath),
                tamperedReleaseProjectPath,
                "--snapshot",
                "active",
                "--dut",
                $"DUT-{suffix}",
                "--actor",
                "runner-process-e2e");
            AssertRunnerFailure(
                tamperedRelease,
                RunnerExitCodes.ProductionRunStartRejected,
                "Conflict.Projects.ProjectReleaseInvalid");

            var draftProjectId = $"project-runner-draft-{suffix}";
            await CreateWorkspaceAsync(
                client,
                draftProjectPath,
                draftProjectId,
                $"application-runner-draft-{suffix}");
            await WriteRunnerConfigurationAsync(draftProjectPath);
            var draftOnly = await RunRunnerProcessAsync(
                GetProjectFilePath(draftProjectPath),
                draftProjectPath,
                "--snapshot",
                "active",
                "--dut",
                $"DUT-{suffix}",
                "--actor",
                "runner-process-e2e");
            AssertRunnerFailure(
                draftOnly,
                RunnerExitCodes.SnapshotSelectionFailed,
                "Runner.ActiveSnapshotMissing");
        }
        finally
        {
            foreach (var projectPath in new[]
                     {
                         publishedProjectPath,
                         missingReleaseProjectPath,
                         tamperedReleaseProjectPath,
                         draftProjectPath
                     })
            {
                var runnerDataPath = ProjectExecutionDataDirectory.ForProjectDirectory(projectPath);
                if (Directory.Exists(runnerDataPath))
                {
                    Directory.Delete(runnerDataPath, recursive: true);
                }
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task<long> CountRunRowsAsync(
        string databasePath,
        string tableName,
        Guid productionRunId)
    {
        var commandText = tableName switch
        {
            "production_runs" =>
                "SELECT COUNT(*) FROM production_runs WHERE run_id = $production_run_id;",
            "trace_records" =>
                "SELECT COUNT(*) FROM trace_records WHERE production_run_id = $production_run_id;",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName))
        };
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$production_run_id", productionRunId.ToString("D"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task<PublishedRunnerProject> PublishRunnableProjectAsync(
        HttpClient client,
        string projectPath,
        string suffix)
    {
        var projectId = $"project-runner-{suffix}";
        var applicationId = $"application-runner-{suffix}";
        var topologyId = $"topology-runner-{suffix}";
        var layoutId = $"layout-runner-{suffix}";
        var processDefinitionId = $"process-runner-{suffix}";
        var processVersionId = $"{processDefinitionId}@1.0.0";
        var configurationSnapshotId = $"configuration-runner-{suffix}";
        var productionLineDefinitionId = $"line-runner-{suffix}";
        var capabilityId = $"capability-runner-{suffix}";
        var bindingId = $"binding-runner-{suffix}";
        var snapshotId = $"snapshot-runner-{suffix}";

        await CreateWorkspaceAsync(client, projectPath, projectId, applicationId);

        var topologiesPath = ProjectTopologiesPath(projectId, applicationId);
        using (var response = await client.PostAsJsonAsync(
                   topologiesPath,
                   new { topologyId, displayName = "Runner E2E Topology" }))
        {
            await AssertStatusAsync(response, HttpStatusCode.Created);
        }

        using (var response = await client.PostAsJsonAsync(
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
                   }))
        {
            await AssertStatusAsync(response, HttpStatusCode.OK);
        }

        using (var response = await client.PostAsJsonAsync(
                   $"{topologiesPath}/{topologyId}/systems",
                   new
                   {
                       systemId = "station.main",
                       parentSystemId = (string?)null,
                       kind = "Station",
                       systemType = "runner.e2e.station",
                       displayName = "Runner E2E Station",
                       requiredCapabilityIds = Array.Empty<string>(),
                       providedCapabilityIds = Array.Empty<string>(),
                       metadata = new Dictionary<string, string>()
                   }))
        {
            await AssertStatusAsync(response, HttpStatusCode.OK);
        }

        using (var response = await client.PostAsJsonAsync(
                   $"{topologiesPath}/{topologyId}/driver-bindings",
                   new
                   {
                       bindingId,
                       capabilityId,
                       providerKind = "Simulator",
                       providerKey = $"simulator.runner.{suffix}"
                   }))
        {
            await AssertStatusAsync(response, HttpStatusCode.OK);
        }

        var layoutsPath = ProjectLayoutsPath(projectId, applicationId);
        using (var response = await client.PostAsJsonAsync(
                   layoutsPath,
                   new
                   {
                       layoutId,
                       topologyId,
                       displayName = "Runner E2E Layout",
                       canvasWidth = 800,
                       canvasHeight = 600,
                       units = "mm"
                   }))
        {
            await AssertStatusAsync(response, HttpStatusCode.Created);
        }

        using (var response = await client.PostAsJsonAsync(
                   $"{layoutsPath}/{layoutId}/elements",
                   new
                   {
                       elementId = "element.station.main",
                       kind = "SystemShape",
                       target = new { kind = "System", targetId = "station.main" },
                       parentElementId = (string?)null,
                       x = 20,
                       y = 30,
                       width = 320,
                       height = 220,
                       rotationDegrees = 0,
                       zIndex = 1,
                       style = new Dictionary<string, string>()
                   }))
        {
            await AssertStatusAsync(response, HttpStatusCode.OK);
        }

        var processesPath = ProjectProcessesPath(projectId, applicationId);
        using (var response = await client.PostAsJsonAsync(
                   processesPath,
                   CreateRuntimeProcessDefinitionRequest(
                       processDefinitionId,
                       processVersionId,
                       capabilityId)))
        {
            await AssertStatusAsync(response, HttpStatusCode.Created);
        }

        using (var response = await client.PostAsync(
                   $"{processesPath}/{processDefinitionId}/publish",
                   content: null))
        {
            await AssertStatusAsync(response, HttpStatusCode.OK);
        }

        await CreatePublishedEngineeringConfigurationAsync(
            client,
            projectId,
            applicationId,
            suffix,
            configurationSnapshotId,
            processDefinitionId,
            processVersionId,
            capabilityId);

        using (var response = await client.PostAsJsonAsync(
                   $"/api/automation-projects/{projectId}/applications/{applicationId}/production-lines",
                   new
                   {
                       lineDefinitionId = productionLineDefinitionId,
                       displayName = "Runner E2E Production Line",
                       topologyId,
                       dutModel = new
                       {
                           dutModelId = $"dut-runner-{suffix}",
                           modelCode = $"MODEL-{suffix}",
                           identityInputKey = "serialNumber"
                       },
                       workstations = new[]
                       {
                           new
                           {
                               workstationId = "workstation.main",
                               displayName = "Runner E2E Workstation",
                               stationSystemId = "station.main"
                           }
                       },
                       stages = new[]
                       {
                           new
                           {
                               stageId = "stage.main",
                               sequence = 1,
                               displayName = "Runner E2E Stage",
                               workstationId = "workstation.main",
                               flowDefinitionId = processDefinitionId,
                               configurationSnapshotId,
                               externalTestProgramAdapterId = (string?)null
                           }
                       },
                       externalTestProgramAdapters = Array.Empty<object>()
                   }))
        {
            await AssertStatusAsync(response, HttpStatusCode.Created);
        }

        using (var response = await client.PutAsJsonAsync(
                   $"/api/automation-projects/{projectId}/applications/{applicationId}/topology",
                   new { topologyId }))
        {
            await AssertStatusAsync(response, HttpStatusCode.OK);
        }

        using (var response = await client.PutAsync(
                   $"/api/automation-projects/{projectId}/applications/{applicationId}/process-definitions/{processDefinitionId}",
                   content: null))
        {
            await AssertStatusAsync(response, HttpStatusCode.OK);
        }

        using var publishResponse = await client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/snapshots",
            new
            {
                snapshotId,
                applicationId,
                productionLineDefinitionId
            });
        using var publishBody = await ReadJsonAsync(publishResponse);
        Assert.Equal(
            HttpStatusCode.Created,
            publishResponse.StatusCode);

        var snapshot = Assert.Single(
            publishBody.RootElement
                .GetProperty("snapshots")
                .EnumerateArray(),
            item => string.Equals(
                    item.GetProperty("snapshotId").GetString(),
                    snapshotId,
                    StringComparison.Ordinal));
        var releaseContentSha256 = snapshot.GetProperty("releaseContentSha256").GetString();
        Assert.NotNull(releaseContentSha256);
        Assert.Equal(64, releaseContentSha256.Length);

        return new PublishedRunnerProject(
            projectId,
            applicationId,
            snapshotId,
            releaseContentSha256,
            GetProjectFilePath(projectPath));
    }

    private static async Task CreateWorkspaceAsync(
        HttpClient client,
        string projectPath,
        string projectId,
        string applicationId)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/automation-project-workspaces",
            new
            {
                projectId,
                displayName = $"Runner E2E {projectId}",
                projectPath,
                defaultApplicationId = applicationId,
                defaultApplicationName = "Runner E2E Application"
            });
        await AssertStatusAsync(response, HttpStatusCode.Created);
    }

    private static async Task CreatePublishedEngineeringConfigurationAsync(
        HttpClient client,
        string projectId,
        string applicationId,
        string suffix,
        string configurationSnapshotId,
        string processDefinitionId,
        string processVersionId,
        string capabilityId)
    {
        var engineeringBase = $"/api/automation-projects/{projectId}/applications/{applicationId}/engineering";
        var workspaceId = $"workspace-runner-{suffix}";
        var recipeId = $"recipe-runner-{suffix}";
        var stationProfileId = $"station-profile-runner-{suffix}";
        var engineeringProjectId = $"engineering-project-runner-{suffix}";

        using (var response = await client.PostAsJsonAsync(
                   $"{engineeringBase}/workspaces",
                   new { workspaceId, displayName = "Runner E2E Workspace" }))
        {
            await AssertStatusAsync(response, HttpStatusCode.Created);
        }

        using (var response = await client.PostAsJsonAsync(
                   $"{engineeringBase}/recipes",
                   new
                   {
                       recipeId,
                       versionId = $"{recipeId}@1.0.0",
                       displayName = "Runner E2E Recipe",
                       parameters = new[] { new { key = "runner.mode", value = "e2e" } }
                   }))
        {
            await AssertStatusAsync(response, HttpStatusCode.Created);
        }

        using (var response = await client.PostAsync(
                   $"{engineeringBase}/recipes/{recipeId}/publish",
                   content: null))
        {
            await AssertStatusAsync(response, HttpStatusCode.OK);
        }

        using (var response = await client.PostAsJsonAsync(
                   $"{engineeringBase}/station-profiles",
                   new
                   {
                       stationProfileId,
                       stationSystemId = "station.main",
                       displayName = "Runner E2E Station",
                       deviceBindings = new[]
                       {
                           new
                           {
                               deviceBindingId = "binding.primary",
                               capabilityId,
                               deviceKey = "runner-simulator"
                           }
                       }
                   }))
        {
            await AssertStatusAsync(response, HttpStatusCode.Created);
        }

        using (var response = await client.PostAsJsonAsync(
                   $"{engineeringBase}/projects",
                   new
                   {
                       projectId = engineeringProjectId,
                       workspaceId,
                       displayName = "Runner E2E Engineering Project"
                   }))
        {
            await AssertStatusAsync(response, HttpStatusCode.Created);
        }

        using (var response = await client.PostAsJsonAsync(
                   $"{engineeringBase}/projects/{engineeringProjectId}/configuration-snapshots",
                   new
                   {
                       snapshotId = configurationSnapshotId,
                       processDefinitionId,
                       processVersionId,
                       recipeId,
                       stationProfileId
                   }))
        {
            await AssertStatusAsync(response, HttpStatusCode.Created);
        }
    }

    private static object CreateRuntimeProcessDefinitionRequest(
        string processDefinitionId,
        string processVersionId,
        string capabilityId)
    {
        return new
        {
            processDefinitionId,
            versionId = processVersionId,
            displayName = "Runner E2E Process",
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
                    inputPayload = (string?)"runner-e2e"
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

    private static async Task<RunnerProcessResult> RunRunnerProcessAsync(
        string projectFilePath,
        string workingDirectory,
        params string[] arguments)
    {
        var runnerAssemblyPath = typeof(RunnerEntrypoint).Assembly.Location;
        Assert.True(File.Exists(runnerAssemblyPath));
        Assert.True(File.Exists(Path.ChangeExtension(runnerAssemblyPath, ".runtimeconfig.json")));

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(runnerAssemblyPath);
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(projectFilePath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["OpenLineOps__Runtime__Persistence__Provider"] = "InMemory";
        startInfo.Environment["OpenLineOps__Devices__Persistence__Provider"] = "InMemory";
        startInfo.Environment["OpenLineOps__Plugins__EventLog__Provider"] = "Sqlite";
        startInfo.Environment["OpenLineOps__Plugins__EventLog__DatabasePath"] =
            Path.Combine(workingDirectory, "runner-plugin-events.sqlite");

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await process.WaitForExitAsync(timeout.Token);

        return new RunnerProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static JsonDocument ParseSingleJsonOutput(RunnerProcessResult result)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(result.StandardOutput),
            $"Runner emitted no JSON. stderr: {result.StandardError}");
        try
        {
            return JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"Runner stdout was not one JSON document. stdout: {result.StandardOutput}; stderr: {result.StandardError}; {exception.Message}");
        }
    }

    private static void AssertRunnerFailure(
        RunnerProcessResult result,
        int expectedExitCode,
        string expectedErrorCode)
    {
        Assert.True(
            result.ExitCode == expectedExitCode,
            $"Runner exited with {result.ExitCode}, expected {expectedExitCode}. "
            + $"stdout: {result.StandardOutput}; stderr: {result.StandardError}");
        using var output = ParseSingleJsonOutput(result);
        var root = output.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(expectedExitCode, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(
            expectedErrorCode,
            root.GetProperty("error").GetProperty("code").GetString());
    }

    private static void TamperReleasedFlow(string projectPath)
    {
        var manifestPath = Assert.Single(Directory.GetFiles(
            Path.Combine(projectPath, "releases"),
            "release.json",
            SearchOption.AllDirectories));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var flowRelativePath = Assert.Single(
            manifest.RootElement
                .GetProperty("files")
                .EnumerateArray()
                .Select(file => file.GetProperty("relativePath").GetString()),
            path => path is not null && path.EndsWith("/flow.json", StringComparison.Ordinal));
        var flowPath = Path.Combine(
            Path.GetDirectoryName(manifestPath)!,
            "source",
            flowRelativePath!.Replace('/', Path.DirectorySeparatorChar));
        File.AppendAllText(flowPath, Environment.NewLine + " ");
    }

    private static async Task WriteRunnerConfigurationAsync(string projectPath)
    {
        var configuration = """
            {
              "OpenLineOps": {
                "Runtime": { "Persistence": { "Provider": "InMemory" } },
                "Traceability": { "Persistence": { "Provider": "InMemory" } },
                "Devices": { "Persistence": { "Provider": "InMemory" } },
                "Plugins": {
                  "Activator": "ManifestOnly",
                  "EventLog": {
                    "Provider": "Sqlite",
                    "DatabasePath": "runner-plugin-events.sqlite"
                  }
                }
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(projectPath, "appsettings.json"), configuration);
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        foreach (var directory in Directory.GetDirectories(
                     sourcePath,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(
                sourcePath,
                destinationPath,
                StringComparison.Ordinal));
        }

        Directory.CreateDirectory(destinationPath);
        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var targetPath = file.Replace(sourcePath, destinationPath, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath);
        }
    }

    private static string GetProjectFilePath(string projectPath)
    {
        return Assert.Single(Directory.GetFiles(
            projectPath,
            "*.oloproj",
            SearchOption.TopDirectoryOnly));
    }

    private static async Task AssertStatusAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        if (response.StatusCode == expectedStatus)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        throw new Xunit.Sdk.XunitException(
            $"Expected HTTP {(int)expectedStatus}, received {(int)response.StatusCode}: {body}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
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

    private sealed record PublishedRunnerProject(
        string ProjectId,
        string ApplicationId,
        string SnapshotId,
        string ReleaseContentSha256,
        string ProjectFilePath);

    private sealed record RunnerProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
