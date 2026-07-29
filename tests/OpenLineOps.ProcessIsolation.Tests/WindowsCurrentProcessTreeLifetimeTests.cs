using System.Diagnostics;
using System.Globalization;

namespace OpenLineOps.ProcessIsolation.Tests;

public sealed class WindowsCurrentProcessTreeLifetimeTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);
    private const int GracefulExitCode = 37;

    [Fact]
    public async Task NaturalRootExitPreservesExitCodeAndTerminatesSpawnedChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var childProcessIdFile = NewTemporaryPath("graceful-child.pid");
        var releaseFile = NewTemporaryPath("graceful-release.signal");
        Process? child = null;
        using var root = StartHelper("graceful-exit", childProcessIdFile, releaseFile);
        try
        {
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var childProcessId = await ReadProcessIdAsync(
                childProcessIdFile,
                timeout.Token);
            child = OpenRunningProcess(childProcessId);

            await File.WriteAllTextAsync(releaseFile, "release", timeout.Token);
            await root.WaitForExitAsync(timeout.Token);

            Assert.Equal(GracefulExitCode, root.ExitCode);
            await AssertProcessExitedAsync(child, timeout.Token);
        }
        finally
        {
            TryTerminate(root);
            if (child is not null)
            {
                TryTerminate(child);
                child.Dispose();
            }
            File.Delete(childProcessIdFile);
            File.Delete(releaseFile);
        }
    }

    [Fact]
    public async Task ForcedRootTerminationClosesJobAndTerminatesSpawnedChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var childProcessIdFile = NewTemporaryPath("forced-child.pid");
        Process? child = null;
        using var root = StartHelper("await-termination", childProcessIdFile);
        try
        {
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var childProcessId = await ReadProcessIdAsync(
                childProcessIdFile,
                timeout.Token);
            child = OpenRunningProcess(childProcessId);

            root.Kill(entireProcessTree: false);
            await root.WaitForExitAsync(timeout.Token);
            await AssertProcessExitedAsync(child, timeout.Token);
        }
        finally
        {
            TryTerminate(root);
            if (child is not null)
            {
                TryTerminate(child);
                child.Dispose();
            }
            File.Delete(childProcessIdFile);
        }
    }

    [Fact]
    public void NonWindowsBindingFailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(
            WindowsCurrentProcessTreeLifetime.BindCurrentProcess);
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
               ?? throw new InvalidOperationException("Process-isolation test helper did not start.");
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
            "OpenLineOps.ProcessIsolation.TestHelper",
            "bin",
            configuration,
            "net10.0",
            "OpenLineOps.ProcessIsolation.TestHelper.exe");
        return File.Exists(executable)
            ? executable
            : throw new FileNotFoundException(
                "The process-isolation test helper apphost was not built.",
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
        while (!File.Exists(path))
        {
            await Task.Delay(10, cancellationToken);
        }

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

    private static async Task AssertProcessExitedAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        if (!process.HasExited)
        {
            await process.WaitForExitAsync(cancellationToken);
        }
    }

    private static Process OpenRunningProcess(int processId)
    {
        var process = Process.GetProcessById(processId);
        try
        {
            _ = process.SafeHandle;
            Assert.False(process.HasExited);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static void TryTerminate(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                throw new TimeoutException("The current-process Job lifetime test host did not stop within 10 seconds.");
            }
        }
    }

}
