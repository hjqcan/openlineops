using System.Text.Json;
using OpenLineOps.Runner;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerEntrypointTests
{
    [Fact]
    public async Task RunWithMissingProjectBuildsModuleProviderAndReturnsProjectOpenFailure()
    {
        var currentDirectory = Path.GetTempPath();
        var missingProject = $"openlineops-missing-project-{Guid.NewGuid():N}";
        var writer = new StringWriter();

        var exitCode = await RunnerEntrypoint.RunAsync(
            ["run", missingProject, "--dut", "DUT-001", "--actor", "runner-test"],
            currentDirectory,
            writer);

        Assert.True(
            exitCode == RunnerExitCodes.ProjectOpenFailed,
            $"Runner returned {exitCode}: {writer}");
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(3, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(
            "NotFound.Projects.ManifestNotFound",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
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
