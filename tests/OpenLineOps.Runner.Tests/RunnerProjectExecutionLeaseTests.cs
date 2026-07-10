using OpenLineOps.Projects.Api.Integrations;

namespace OpenLineOps.Runner.Tests;

public sealed class ProjectExecutionCoordinatorTests
{
    [Fact]
    public void ProjectDirectoryAndProjectFileShareOneDurableDataScope()
    {
        var projectDirectory = Path.Combine(Path.GetTempPath(), "openlineops-runner-scope", "line-a");
        var projectFile = Path.Combine(projectDirectory, "line-a.oloproj");

        var fromDirectory = ProjectExecutionDataDirectory.FromProjectTarget(
            projectDirectory,
            Directory.GetCurrentDirectory());
        var fromFile = ProjectExecutionDataDirectory.FromProjectTarget(
            projectFile,
            Directory.GetCurrentDirectory());

        Assert.Equal(fromDirectory, fromFile);
    }

    [Fact]
    public async Task ProjectExecutionLeaseIsExclusiveAndCanBeReacquiredAfterRelease()
    {
        var projectDirectory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-runner-lease-tests",
            Guid.NewGuid().ToString("N"));
        var dataDirectory = ProjectExecutionDataDirectory.ForProjectDirectory(projectDirectory);
        await using var firstCoordinator = new ProjectExecutionCoordinator();
        await using var secondCoordinator = new ProjectExecutionCoordinator();

        try
        {
            await using (var first = Assert.IsAssignableFrom<IProjectExecutionLease>(
                             await firstCoordinator.TryAcquireAsync(projectDirectory)))
            {
                await using var reentrant = Assert.IsAssignableFrom<IProjectExecutionLease>(
                    await firstCoordinator.TryAcquireAsync(projectDirectory));
                Assert.Null(await secondCoordinator.TryAcquireAsync(projectDirectory));
            }

            await using (var reacquired = Assert.IsAssignableFrom<IProjectExecutionLease>(
                             await secondCoordinator.TryAcquireAsync(projectDirectory)))
            {
                Assert.NotNull(reacquired);
            }
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }
}
