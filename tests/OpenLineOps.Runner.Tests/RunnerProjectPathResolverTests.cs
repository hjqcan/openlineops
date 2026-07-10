using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Runner;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerProjectPathResolverTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-runner-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ResolveProjectDirectoryAcceptsAutomationProjectFilePath()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var manifestPath = Path.Combine(
            _temporaryDirectory,
            AutomationProjectFileConvention.GetProjectFileName("line-a"));
        File.WriteAllText(manifestPath, "{}");

        var result = RunnerProjectPathResolver.ResolveProjectDirectory(
            manifestPath,
            Directory.GetCurrentDirectory());

        Assert.Equal(Path.GetFullPath(_temporaryDirectory), result);
    }

    [Fact]
    public void ResolveProjectDirectoryRejectsOtherExistingFiles()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var otherFile = Path.Combine(_temporaryDirectory, "other.json");
        File.WriteAllText(otherFile, "{}");

        var exception = Assert.Throws<InvalidDataException>(() =>
            RunnerProjectPathResolver.ResolveProjectDirectory(
                otherFile,
                Directory.GetCurrentDirectory()));

        Assert.Contains(AutomationProjectFileConvention.ProjectFileExtension, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProjectDirectoryAcceptsLegacyManifestPath()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var manifestPath = Path.Combine(
            _temporaryDirectory,
            AutomationProjectFileConvention.LegacyProjectFileName);
        File.WriteAllText(manifestPath, "{}");

        var result = RunnerProjectPathResolver.ResolveProjectDirectory(
            manifestPath,
            Directory.GetCurrentDirectory());

        Assert.Equal(Path.GetFullPath(_temporaryDirectory), result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
