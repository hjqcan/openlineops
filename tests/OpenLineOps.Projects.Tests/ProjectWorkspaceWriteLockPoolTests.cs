using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Projects.Tests;

public sealed class ProjectWorkspaceWriteLockPoolTests
{
    [Fact]
    public async Task UniqueArtifactKeysAreReclaimedAfterEveryLease()
    {
        var pool = new ProjectWorkspaceWriteLockPool();
        var root = Path.Combine(
            Path.GetTempPath(),
            "openlineops-workspace-write-locks",
            Guid.NewGuid().ToString("N"));

        for (var index = 0; index < 2_000; index++)
        {
            using var lease = await pool.AcquireAsync(
                Path.Combine(root, $"artifact-{index:D4}.json"));
        }

        Assert.Equal(0, pool.RetainedKeyCount);
    }

    [Fact]
    public async Task CanceledWaiterDoesNotRetireHeldEntryOrLeakItsReference()
    {
        var pool = new ProjectWorkspaceWriteLockPool();
        var key = Path.Combine(
            Path.GetTempPath(),
            "openlineops-workspace-write-locks",
            Guid.NewGuid().ToString("N"),
            "resource.json");
        var heldLease = await pool.AcquireAsync(key);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pool.AcquireAsync(key, cancellation.Token));
        Assert.Equal(1, pool.RetainedKeyCount);

        heldLease.Dispose();
        Assert.Equal(0, pool.RetainedKeyCount);

        using var nextLease = await pool.AcquireAsync(key);
        Assert.Equal(1, pool.RetainedKeyCount);
    }
}
