using System.Diagnostics;
using System.Globalization;

namespace OpenLineOps.ProcessIsolation.Tests;

public sealed class ProcessTreeHostTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task StandardInputIsForwardedAndCleanExitIsPreserved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(ProcessTimeout);
        using var host = StartHost(
            redirectStandardInput: true,
            redirectStandardOutput: true,
            "echo-standard-input");
        const string message = "controlled shutdown\n";
        await host.StandardInput.WriteAsync(message);
        host.StandardInput.Close();

        var output = await host.StandardOutput
            .ReadToEndAsync(timeout.Token);
        await host.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, host.ExitCode);
        Assert.Equal(message, output);
    }

    [Fact]
    public async Task MismatchedOwnerCreationIdentityFailsBeforeChildLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var timeout = new CancellationTokenSource(ProcessTimeout);
        using var host = StartHost(
            CurrentProcessStartedAtUnixMilliseconds() + 1,
            redirectStandardInput: false,
            redirectStandardOutput: false,
            "echo-standard-input");
        var standardError = await host.StandardError
            .ReadToEndAsync(timeout.Token);
        await host.WaitForExitAsync(timeout.Token);

        Assert.Equal(64, host.ExitCode);
        Assert.DoesNotContain(
            "OPENLINEOPS_PROCESS_TREE_ROOT",
            standardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForcedHostTerminationClosesJobAndKillsTheHostedTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var childProcessIdFile = NewTemporaryPath("hosted-child.pid");
        Process? hostedRoot = null;
        Process? hostedChild = null;
        using var host = StartHost(childProcessIdFile);
        try
        {
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var hostedRootProcessId = await ReadHostedRootProcessIdAsync(
                host,
                timeout.Token);
            var hostedChildProcessId = await ReadProcessIdAsync(
                childProcessIdFile,
                timeout.Token);
            hostedRoot = OpenRunningProcess(hostedRootProcessId);
            hostedChild = OpenRunningProcess(hostedChildProcessId);

            host.Kill(entireProcessTree: false);
            await host.WaitForExitAsync(timeout.Token);

            await AssertProcessExitedAsync(hostedRoot, timeout.Token);
            await AssertProcessExitedAsync(hostedChild, timeout.Token);
        }
        finally
        {
            TryTerminate(host);
            if (hostedRoot is not null)
            {
                TryTerminate(hostedRoot);
                hostedRoot.Dispose();
            }
            if (hostedChild is not null)
            {
                TryTerminate(hostedChild);
                hostedChild.Dispose();
            }
            File.Delete(childProcessIdFile);
        }
    }

    [Fact]
    public async Task OwnerExitTerminatesDescendantsAfterHostedRootHasExited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var childProcessIdFile = NewTemporaryPath("owner-child.pid");
        var hostProcessIdFile = NewTemporaryPath("owner-host.pid");
        var hostedRootProcessIdFile = NewTemporaryPath("owner-root.pid");
        Process? hostProcess = null;
        Process? hostedChild = null;
        using var owner = StartOwner(
            childProcessIdFile,
            hostProcessIdFile,
            hostedRootProcessIdFile);
        try
        {
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var hostProcessId = await ReadProcessIdAsync(
                hostProcessIdFile,
                timeout.Token);
            var hostedRootIdentity = await ReadProcessIdentityAsync(
                hostedRootProcessIdFile,
                timeout.Token);
            var hostedChildProcessId = await ReadProcessIdAsync(
                childProcessIdFile,
                timeout.Token);
            hostProcess = OpenRunningProcess(hostProcessId);
            hostedChild = OpenRunningProcess(hostedChildProcessId);
            await AssertProcessExitedAsync(
                hostedRootIdentity,
                timeout.Token);

            owner.Kill(entireProcessTree: false);
            await owner.WaitForExitAsync(timeout.Token);

            await AssertProcessExitedAsync(hostProcess, timeout.Token);
            await AssertProcessExitedAsync(hostedChild, timeout.Token);
        }
        finally
        {
            TryTerminate(owner);
            foreach (var process in new[] { hostProcess, hostedChild })
            {
                if (process is not null)
                {
                    TryTerminate(process);
                    process.Dispose();
                }
            }
            File.Delete(childProcessIdFile);
            File.Delete(hostProcessIdFile);
            File.Delete(hostedRootProcessIdFile);
        }
    }

    private static Process StartHost(string childProcessIdFile)
    {
        return StartHost(
            redirectStandardInput: false,
            redirectStandardOutput: false,
            "await-termination",
            childProcessIdFile);
    }

    private static Process StartOwner(
        string childProcessIdFile,
        string hostProcessIdFile,
        string hostedRootProcessIdFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = TestHelperExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("own-process-tree-host");
        startInfo.ArgumentList.Add(ProcessTreeHostExecutablePath());
        startInfo.ArgumentList.Add(TestHelperExecutablePath());
        startInfo.ArgumentList.Add(FindRepositoryRoot());
        startInfo.ArgumentList.Add(childProcessIdFile);
        startInfo.ArgumentList.Add(hostProcessIdFile);
        startInfo.ArgumentList.Add(hostedRootProcessIdFile);
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException(
                   "Process Tree Host owner did not start.");
    }

    private static Process StartHost(
        bool redirectStandardInput,
        bool redirectStandardOutput,
        params string[] childArguments)
    {
        return StartHost(
            CurrentProcessStartedAtUnixMilliseconds(),
            redirectStandardInput,
            redirectStandardOutput,
            childArguments);
    }

    private static Process StartHost(
        long ownerStartedAtUnixMilliseconds,
        bool redirectStandardInput,
        bool redirectStandardOutput,
        params string[] childArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ProcessTreeHostExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(
            ownerStartedAtUnixMilliseconds
                .ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(TestHelperExecutablePath());
        startInfo.ArgumentList.Add(FindRepositoryRoot());
        foreach (var argument in childArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException(
                   "Process Tree Host did not start.");
    }

    private static async Task<int> ReadHostedRootProcessIdAsync(
        Process host,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await host.StandardError
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidDataException(
                    "Process Tree Host exited before reporting its child.");
            }

            const string prefix = "OPENLINEOPS_PROCESS_TREE_ROOT ";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                var fields = line[prefix.Length..].Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 2
                    && int.TryParse(
                        fields[0],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var processId)
                    && processId > 0
                    && long.TryParse(
                        fields[1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var startedAtUnixMilliseconds)
                    && startedAtUnixMilliseconds > 0)
                {
                    return processId;
                }
            }
        }
    }

    private static string ProcessTreeHostExecutablePath() =>
        BuiltExecutablePath(
            "tools",
            "OpenLineOps.ProcessTreeHost",
            "OpenLineOps.ProcessTreeHost.exe");

    private static long CurrentProcessStartedAtUnixMilliseconds()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime())
            .ToUnixTimeMilliseconds();
    }

    private static string TestHelperExecutablePath() =>
        BuiltExecutablePath(
            "tests",
            "OpenLineOps.ProcessIsolation.TestHelper",
            "OpenLineOps.ProcessIsolation.TestHelper.exe");

    private static string BuiltExecutablePath(
        string category,
        string projectName,
        string executableName)
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var executable = Path.Combine(
            FindRepositoryRoot(),
            category,
            projectName,
            "bin",
            configuration,
            "net10.0",
            executableName);
        return File.Exists(executable)
            ? executable
            : throw new FileNotFoundException(
                $"{projectName} apphost was not built.",
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
               ?? throw new DirectoryNotFoundException(
                   "OpenLineOps repository root was not found.");
    }

    private static string NewTemporaryPath(string suffix) =>
        Path.Combine(
            Path.GetTempPath(),
            $"openlineops-{Guid.NewGuid():N}-{suffix}");

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
                var value = await File.ReadAllTextAsync(
                    path,
                    cancellationToken);
                return int.Parse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture);
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

    private static async Task AssertProcessExitedAsync(
        ProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }
        using (process)
        {
            long actualStartedAtUnixMilliseconds;
            try
            {
                actualStartedAtUnixMilliseconds = new DateTimeOffset(
                        process.StartTime.ToUniversalTime())
                    .ToUnixTimeMilliseconds();
            }
            catch (InvalidOperationException)
            {
                return;
            }
            if (actualStartedAtUnixMilliseconds
                != identity.StartedAtUnixMilliseconds)
            {
                return;
            }
            await AssertProcessExitedAsync(process, cancellationToken);
        }
    }

    private static async Task<ProcessIdentity> ReadProcessIdentityAsync(
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
                var fields = (await File.ReadAllTextAsync(
                        path,
                        cancellationToken))
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 2
                    && int.TryParse(
                        fields[0],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var processId)
                    && processId > 0
                    && long.TryParse(
                        fields[1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var startedAtUnixMilliseconds)
                    && startedAtUnixMilliseconds > 0)
                {
                    return new ProcessIdentity(
                        processId,
                        startedAtUnixMilliseconds);
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(10, cancellationToken);
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
                throw new TimeoutException("The ProcessTreeHost test process did not stop within 10 seconds.");
            }
        }
    }

    private sealed record ProcessIdentity(
        int ProcessId,
        long StartedAtUnixMilliseconds);
}
