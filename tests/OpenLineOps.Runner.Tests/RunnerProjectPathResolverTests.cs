using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Runner;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerProjectPathResolverTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-runner-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ResolveProjectTargetPreservesAutomationProjectFilePath()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var manifestPath = Path.Combine(
            _temporaryDirectory,
            AutomationProjectFileConvention.GetProjectFileName("line-a"));
        File.WriteAllText(manifestPath, "{}");

        var result = RunnerProjectPathResolver.ResolveProjectTarget(
            manifestPath,
            Directory.GetCurrentDirectory());

        Assert.Equal(Path.GetFullPath(manifestPath), result);
    }

    [Fact]
    public void ResolveProjectTargetRejectsOtherExistingFiles()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var otherFile = Path.Combine(_temporaryDirectory, "other.json");
        File.WriteAllText(otherFile, "{}");

        var exception = Assert.Throws<InvalidDataException>(() =>
            RunnerProjectPathResolver.ResolveProjectTarget(
                otherFile,
                Directory.GetCurrentDirectory()));

        Assert.Contains(AutomationProjectFileConvention.ProjectFileExtension, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProjectTargetAcceptsExistingDirectory()
    {
        Directory.CreateDirectory(_temporaryDirectory);

        var result = RunnerProjectPathResolver.ResolveProjectTarget(
            _temporaryDirectory,
            Directory.GetCurrentDirectory());

        Assert.Equal(Path.GetFullPath(_temporaryDirectory), result);
    }

    [Fact]
    public void ResolveProjectTargetRejectsMissingFileWithWrongExtension()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            RunnerProjectPathResolver.ResolveProjectTarget(
                Path.Combine(_temporaryDirectory, "missing.json"),
                Directory.GetCurrentDirectory()));

        Assert.Contains(".oloproj", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProjectTargetRejectsJsonProjectFile()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var manifestPath = Path.Combine(
            _temporaryDirectory,
            "openlineops.project.json");
        File.WriteAllText(manifestPath, "{}");

        var exception = Assert.Throws<InvalidDataException>(() =>
            RunnerProjectPathResolver.ResolveProjectTarget(
                manifestPath,
                Directory.GetCurrentDirectory()));

        Assert.Contains(".oloproj", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
