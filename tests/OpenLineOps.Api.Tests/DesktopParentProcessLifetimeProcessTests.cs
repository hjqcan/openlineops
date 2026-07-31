using System.Diagnostics;
using System.Globalization;

namespace OpenLineOps.Api.Tests;

public sealed class DesktopParentProcessLifetimeProcessTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(25);
    private const int GracefulRootExitCode = 41;

    [Fact]
    public async Task ParentExitAllowsCompletedApiShutdownAndCleansDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await RunParentExitScenarioAsync(
            "graceful",
            async (root, child, stopRequestedFile, cancellationToken) =>
            {
                await root.WaitForExitAsync(cancellationToken);
                Assert.Equal(GracefulRootExitCode, root.ExitCode);
                await WaitForFileAsync(stopRequestedFile, cancellationToken);
                await AssertProcessExitedAsync(child, cancellationToken);
            });
    }

    [Fact]
    public async Task ParentExitFailFastTerminatesStuckApiAndItsDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await RunParentExitScenarioAsync(
            "stuck",
            async (root, child, stopRequestedFile, cancellationToken) =>
            {
                await WaitForFileAsync(stopRequestedFile, cancellationToken);
                var deadline = Stopwatch.StartNew();
                await root.WaitForExitAsync(cancellationToken);

                Assert.NotEqual(0, root.ExitCode);
                Assert.NotEqual(GracefulRootExitCode, root.ExitCode);
                Assert.InRange(
                    deadline.Elapsed,
                    TimeSpan.FromSeconds(7),
                    DesktopParentProcessLifetime.ParentExitShutdownDeadline
                    + TimeSpan.FromSeconds(3));
                await AssertProcessExitedAsync(child, cancellationToken);
            });
    }

    [Fact]
    public async Task ParentExitFailFastDeadlineSurvivesBlockedStoppingCallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await RunParentExitScenarioAsync(
            "callback-stuck",
            async (root, child, stopRequestedFile, cancellationToken) =>
            {
                await WaitForFileAsync(stopRequestedFile, cancellationToken);
                var deadline = Stopwatch.StartNew();
                await root.WaitForExitAsync(cancellationToken);

                Assert.NotEqual(0, root.ExitCode);
                Assert.NotEqual(GracefulRootExitCode, root.ExitCode);
                Assert.InRange(
                    deadline.Elapsed,
                    TimeSpan.FromSeconds(7),
                    DesktopParentProcessLifetime.ParentExitShutdownDeadline
                    + TimeSpan.FromSeconds(3));
                await AssertProcessExitedAsync(child, cancellationToken);
            });
    }

    [Fact]
    public async Task ParentExitDuringBlockedApiStartupTerminatesApiAndItsDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await RunParentExitScenarioAsync(
            "startup-stuck",
            async (root, child, stopRequestedFile, cancellationToken) =>
            {
                await WaitForFileAsync(stopRequestedFile, cancellationToken);
                var deadline = Stopwatch.StartNew();
                await root.WaitForExitAsync(cancellationToken);

                Assert.NotEqual(0, root.ExitCode);
                Assert.NotEqual(GracefulRootExitCode, root.ExitCode);
                Assert.InRange(
                    deadline.Elapsed,
                    TimeSpan.FromSeconds(7),
                    DesktopParentProcessLifetime.ParentExitShutdownDeadline
                    + TimeSpan.FromSeconds(3));
                await AssertProcessExitedAsync(child, cancellationToken);
            });
    }

    private static async Task RunParentExitScenarioAsync(
        string shutdownMode,
        Func<Process, Process, string, CancellationToken, Task> assertAfterParentExit)
    {
        var rootProcessIdFile = NewTemporaryPath($"{shutdownMode}-root.pid");
        var childProcessIdFile = NewTemporaryPath($"{shutdownMode}-child.pid");
        var stopRequestedFile = NewTemporaryPath($"{shutdownMode}-stop.signal");
        var releaseParentFile = NewTemporaryPath($"{shutdownMode}-release.signal");
        Process? root = null;
        Process? child = null;
        using var parent = StartHelper(
            "parent",
            shutdownMode,
            rootProcessIdFile,
            childProcessIdFile,
            stopRequestedFile,
            releaseParentFile);
        try
        {
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var rootProcessId = await ReadProcessIdAsync(
                rootProcessIdFile,
                timeout.Token);
            var childProcessId = await ReadProcessIdAsync(
                childProcessIdFile,
                timeout.Token);
            root = Process.GetProcessById(rootProcessId);
            child = Process.GetProcessById(childProcessId);
            _ = root.SafeHandle;
            _ = child.SafeHandle;
            Assert.False(root.HasExited);
            Assert.False(child.HasExited);

            await File.WriteAllTextAsync(
                releaseParentFile,
                "release",
                timeout.Token);
            await parent.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, parent.ExitCode);

            await assertAfterParentExit(
                root,
                child,
                stopRequestedFile,
                timeout.Token);
        }
        finally
        {
            TryTerminate(parent);
            TryTerminate(root);
            TryTerminate(child);
            root?.Dispose();
            child?.Dispose();
            File.Delete(rootProcessIdFile);
            File.Delete(childProcessIdFile);
            File.Delete(stopRequestedFile);
            File.Delete(releaseParentFile);
        }
    }

    private static Process StartHelper(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = HelperExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("API lifetime test helper did not start.");
    }

    private static string HelperExecutablePath()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var executable = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "OpenLineOps.Api.TestHelper",
            "bin",
            configuration,
            "net10.0",
            "OpenLineOps.Api.TestHelper.exe");
        return File.Exists(executable)
            ? executable
            : throw new FileNotFoundException(
                "The API lifetime test helper apphost was not built.",
                executable);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "OpenLineOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("OpenLineOps repository root was not found.");
    }

    private static string NewTemporaryPath(string suffix) =>
        Path.Combine(Path.GetTempPath(), $"openlineops-{Guid.NewGuid():N}-{suffix}");

    private static async Task<int> ReadProcessIdAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await WaitForFileAsync(path, cancellationToken);
        while (true)
        {
            try
            {
                var value = await File.ReadAllTextAsync(path, cancellationToken);
                return int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            catch (IOException)
            {
                await Task.Delay(10, cancellationToken);
            }
        }
    }

    private static async Task WaitForFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task AssertProcessExitedAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        if (!process.HasExited)
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        Assert.True(process.HasExited);
    }

    private static void TryTerminate(Process? process)
    {
        if (process is not null && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                throw new TimeoutException("The desktop parent lifetime integration host did not stop within 10 seconds.");
            }
        }
    }
}
