using System.Diagnostics;
using System.Globalization;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Infrastructure.Execution;
using OpenLineOps.Agent.SafetyActuator.TestHelper;

namespace OpenLineOps.Agent.Tests;

public sealed class ProcessStationSafetyActuatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-safety-actuator-{Guid.NewGuid():N}");

    [Fact]
    public void UnsupportedTimeoutIsRejectedBeforeExecution()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateActuator(TimeSpan.FromDays(60)));
    }

    [Fact]
    public async Task MismatchedCreationIdentityDoesNotTerminatePidCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var identityFile = Path.Combine(_root, "identity-mismatch.pid");
        var assembly = typeof(SafetyActuatorTestHelperMarker).Assembly.Location;
        var executable = Path.ChangeExtension(assembly, ".exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("safety-child");
        startInfo.ArgumentList.Add(identityFile);
        startInfo.ArgumentList.Add("300000");
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                "Safety identity test process did not start.");
        try
        {
            var actualIdentity = await WaitForProcessIdentityAsync(identityFile);
            Assert.Equal(process.Id, actualIdentity.ProcessId);
            var reusedPidIdentity = actualIdentity with
            {
                StartedAtUnixMilliseconds =
                    actualIdentity.StartedAtUnixMilliseconds + 1
            };

            Assert.Null(TryOpenRunningProcess(reusedPidIdentity));
            await TerminateProcessAsync(reusedPidIdentity);
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task CleanProcessTreeCompletesSuccessfully()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var actuator = CreateActuator(TimeSpan.FromSeconds(5));

        var result = await actuator.EmergencyStopAsync(
            CreateRequest("clean-success", "operator"));

        Assert.True(result.Accepted);
        Assert.Null(result.FailureCode);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task NonzeroExitIsExecutionFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var actuator = CreateActuator(TimeSpan.FromSeconds(5));

        var result = await actuator.EmergencyStopAsync(
            CreateRequest("execution-failure", "operator"));

        Assert.False(result.Accepted);
        Assert.Equal("Agent.SafetyFailed", result.FailureCode);
        Assert.Contains("code 23", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SafetyJobEnforcesItsActiveProcessLimit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var observationFile = Path.Combine(_root, "process-limit.txt");
        var actuator = CreateActuator(TimeSpan.FromSeconds(15));

        var result = await actuator.EmergencyStopAsync(
            CreateRequest("process-limit", observationFile));

        Assert.True(result.Accepted);
        var observation = await File.ReadAllTextAsync(observationFile);
        var fields = observation.Split(':', StringSplitOptions.None);
        Assert.Equal(2, fields.Length);
        Assert.True(
            int.TryParse(
                fields[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var startedProcessCount));
        Assert.InRange(startedProcessCount, 1, 15);
        Assert.True(bool.Parse(fields[1]));
    }

    [Fact]
    public async Task RootExitWaitsUntilEntireJobDrains()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var childProcessIdFile = Path.Combine(_root, "root-exit-child.pid");
        var actuator = CreateActuator(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();

        var result = await actuator.EmergencyStopAsync(
            CreateRequest("root-exit-child", childProcessIdFile));

        stopwatch.Stop();
        var childIdentity = await ReadProcessIdentityAsync(childProcessIdFile);
        Assert.True(result.Accepted);
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(500),
            $"Actuator returned before its descendant drained: {stopwatch.Elapsed}.");
        Assert.False(IsProcessRunning(childIdentity));
    }

    [Fact]
    public async Task TimeoutTerminatesAndDrainsEntireJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var childProcessIdFile = Path.Combine(_root, "timeout-child.pid");
        var actuator = CreateActuator(TimeSpan.FromMilliseconds(500));
        var execution = actuator.EmergencyStopAsync(
                CreateRequest("wait-for-termination", childProcessIdFile))
            .AsTask();
        var childIdentity = await WaitForProcessIdentityAsync(
            childProcessIdFile);
        using var child = TryOpenRunningProcess(childIdentity);
        try
        {
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(result.Accepted);
            Assert.Equal("Agent.SafetyTimedOut", result.FailureCode);
            if (child is not null)
            {
                Assert.True(await WaitUntilProcessExitsAsync(child));
            }
            Assert.False(IsProcessRunning(childIdentity));
        }
        finally
        {
            await TerminateProcessAsync(childIdentity, child);
        }
    }

    [Fact]
    public async Task CancellationTerminatesAndDrainsEntireJobBeforePropagating()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var childProcessIdFile = Path.Combine(_root, "cancel-child.pid");
        var actuator = CreateActuator(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        var execution = actuator.EmergencyStopAsync(
                CreateRequest("wait-for-termination", childProcessIdFile),
                cancellation.Token)
            .AsTask();
        var childIdentity = await WaitForProcessIdentityAsync(
            childProcessIdFile);
        using var child = OpenRunningProcess(childIdentity);
        try
        {
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await execution.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await WaitUntilProcessExitsAsync(child));
        }
        finally
        {
            cancellation.Cancel();
            await TerminateProcessAsync(childIdentity, child);
        }
    }

    [Fact]
    public async Task PreCanceledRequestDoesNotLaunchSafetyProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var childProcessIdFile = Path.Combine(_root, "pre-canceled-child.pid");
        var actuator = CreateActuator(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await actuator.EmergencyStopAsync(
                CreateRequest("wait-for-termination", childProcessIdFile),
                cancellation.Token));
        await Task.Delay(100);
        Assert.False(File.Exists(childProcessIdFile));
    }

    [Fact]
    public async Task OwnerDeathClosesJobAndKillsEntireSafetyProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var assembly = typeof(SafetyActuatorTestHelperMarker).Assembly.Location;
        var executable = Path.ChangeExtension(assembly, ".exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("actuator-owner");
        startInfo.ArgumentList.Add(_root);
        using var owner = Process.Start(startInfo)
                          ?? throw new InvalidOperationException(
                              "Safety actuator owner test process did not start.");
        ProcessIdentity? safetyRootIdentity = null;
        ProcessIdentity? safetyChildIdentity = null;
        Process? safetyRoot = null;
        Process? safetyChild = null;
        try
        {
            safetyRootIdentity = await WaitForProcessIdentityAsync(
                Path.Combine(_root, "safety-root.pid"));
            safetyChildIdentity = await WaitForProcessIdentityAsync(
                Path.Combine(_root, "safety-child.pid"));
            safetyRoot = OpenRunningProcess(safetyRootIdentity);
            safetyChild = OpenRunningProcess(safetyChildIdentity);

            owner.Kill();
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(await WaitUntilProcessExitsAsync(safetyRoot));
            Assert.True(await WaitUntilProcessExitsAsync(safetyChild));
        }
        finally
        {
            try
            {
                if (!owner.HasExited)
                {
                    owner.Kill();
                    await owner.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (InvalidOperationException)
            {
            }

            if (safetyRootIdentity is not null && safetyRoot is not null)
            {
                await TerminateProcessAsync(
                    safetyRootIdentity,
                    safetyRoot);
                safetyRoot.Dispose();
            }
            if (safetyChildIdentity is not null && safetyChild is not null)
            {
                await TerminateProcessAsync(
                    safetyChildIdentity,
                    safetyChild);
                safetyChild.Dispose();
            }
        }
    }

    private ProcessStationSafetyActuator CreateActuator(TimeSpan timeout)
    {
        Directory.CreateDirectory(_root);
        var assembly = typeof(SafetyActuatorTestHelperMarker).Assembly.Location;
        var executable = Path.ChangeExtension(assembly, ".exe");
        Assert.True(
            File.Exists(executable),
            $"Safety actuator test helper apphost is missing: {executable}");
        return new ProcessStationSafetyActuator(
            new ProcessStationSafetyOptions(
                executable,
                _root,
                timeout));
    }

    private static EmergencyStopRequested CreateRequest(
        string reason,
        string requestedBy) =>
        new(
            Guid.NewGuid(),
            $"safety/{Guid.NewGuid():N}",
            "agent-main",
            "station-main",
            reason,
            requestedBy,
            DateTimeOffset.UtcNow);

    private static async Task<ProcessIdentity> WaitForProcessIdentityAsync(
        string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    return await ReadProcessIdentityAsync(path);
                }
                catch (IOException)
                {
                }
                catch (FormatException)
                {
                }
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Safety actuator child process identity was not published: {path}");
    }

    private static async Task<ProcessIdentity> ReadProcessIdentityAsync(
        string path)
    {
        var text = await File.ReadAllTextAsync(path);
        var fields = text.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 2
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
               && startedAtUnixMilliseconds > 0
            ? new ProcessIdentity(
                processId,
                startedAtUnixMilliseconds)
            : throw new FormatException(
                $"Safety actuator child process identity is invalid: {text}");
    }

    private static async Task<bool> WaitUntilProcessExitsAsync(Process process)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return process.HasExited;
    }

    private static async Task TerminateProcessAsync(
        ProcessIdentity identity,
        Process? observedProcess = null)
    {
        var process = observedProcess ?? TryOpenRunningProcess(identity);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            if (observedProcess is null)
            {
                process.Dispose();
            }
        }
    }

    private static Process OpenRunningProcess(ProcessIdentity identity) =>
        TryOpenRunningProcess(identity)
        ?? throw new InvalidOperationException(
            $"Safety actuator child process {identity.ProcessId} is no longer "
            + "running with its published creation identity.");

    private static Process? TryOpenRunningProcess(ProcessIdentity identity)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
            _ = process.SafeHandle;
            var actualStartedAtUnixMilliseconds = new DateTimeOffset(
                    process.StartTime.ToUniversalTime())
                .ToUnixTimeMilliseconds();
            if (actualStartedAtUnixMilliseconds
                    != identity.StartedAtUnixMilliseconds
                || process.HasExited)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (ArgumentException)
        {
            process?.Dispose();
            return null;
        }
        catch (InvalidOperationException)
        {
            process?.Dispose();
            return null;
        }
        catch
        {
            process?.Dispose();
            throw;
        }
    }

    private static bool IsProcessRunning(ProcessIdentity identity)
    {
        using var process = TryOpenRunningProcess(identity);
        return process is not null;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(25);
            }
        }
    }

    private sealed record ProcessIdentity(
        int ProcessId,
        long StartedAtUnixMilliseconds);
}
