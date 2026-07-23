using System.Text.Json;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Runner;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerEntrypointTests
{
    [Fact]
    public void HeadlessConfigurationKeepsWorkspaceAndEvidenceInsideProjectExecutionData()
    {
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-runner-configuration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            var options = new RunnerRunOptions(
                Path.Combine(projectDirectory, "production-line.oloproj"),
                "active",
                Guid.NewGuid(),
                Guid.NewGuid(),
                "UNIT-CONFIGURATION",
                "runner.configuration-test");

            var configuration = RunnerEntrypoint.BuildConfiguration(
                options,
                Path.GetTempPath());

            var executionDataDirectory =
                ProjectExecutionDataDirectory.ForProjectDirectory(projectDirectory);
            var artifactRoot = configuration[
                "OpenLineOps:Traceability:ArtifactStorage:RootPath"];
            var evidenceRoot = configuration[
                "OpenLineOps:Devices:ExternalProgramHost:EvidenceRootPath"];
            var workspaceRoot = configuration[
                "OpenLineOps:Devices:ExternalProgramHost:WorkspaceRootPath"];

            Assert.Equal(Path.Combine(executionDataDirectory, "trace-artifacts"), artifactRoot);
            Assert.Equal(artifactRoot, evidenceRoot);
            Assert.Equal(
                Path.Combine(executionDataDirectory, "external-program-workspaces"),
                workspaceRoot);
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunWithMissingProjectBuildsModuleProviderAndReturnsProjectOpenFailure()
    {
        var missingProject = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-missing-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(missingProject);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(missingProject, "appsettings.json"),
                """
                {
                  "OpenLineOps": {
                    "Runtime": {
                      "Persistence": { "Provider": "InMemory" },
                      "Coordination": { "Provider": "InMemory" },
                      "AgentTransport": { "Provider": "Disabled" },
                      "StationExecution": { "Provider": "InProcess" }
                    },
                    "Traceability": { "Persistence": { "Provider": "InMemory" } },
                    "Devices": { "Persistence": { "Provider": "InMemory" } },
                    "Plugins": {
                      "EventLog": {
                        "Provider": "Sqlite",
                        "DatabasePath": "runner-plugin-events.sqlite"
                      }
                    }
                  }
                }
                """);
            var writer = new StringWriter();

            var exitCode = await RunnerEntrypoint.RunAsync(
                [
                    "run",
                    missingProject,
                    "--production-unit-id",
                    "00000000-0000-0000-0000-000000000001",
                    "--identity",
                    "UNIT-001",
                    "--actor",
                    "runner-test"
                ],
                Path.GetTempPath(),
                writer);

            Assert.True(
                exitCode == RunnerExitCodes.ProjectOpenFailed,
                $"Runner returned {exitCode}: {writer}");
            using var document = JsonDocument.Parse(writer.ToString());
            Assert.False(document.RootElement.TryGetProperty("schemaVersion", out _));
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(3, document.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(
                "NotFound.Projects.ManifestNotFound",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            Directory.Delete(missingProject, recursive: true);
        }
    }

    [Fact]
    public async Task UsageErrorIsMachineReadableAndDoesNotBuildServices()
    {
        var writer = new StringWriter();

        var exitCode = await RunnerEntrypoint.RunAsync(
            [],
            Directory.GetCurrentDirectory(),
            writer);

        Assert.Equal(RunnerExitCodes.UsageError, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.Equal("Runner.UsageError", document.RootElement
            .GetProperty("error")
            .GetProperty("code")
            .GetString());
    }
}
