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
        var childProcessId = await ReadProcessIdAsync(childProcessIdFile);
        Assert.True(result.Accepted);
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(500),
            $"Actuator returned before its descendant drained: {stopwatch.Elapsed}.");
        Assert.False(IsProcessRunning(childProcessId));
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
        var childProcessId = await WaitForProcessIdAsync(childProcessIdFile);
        using var child = OpenRunningProcess(childProcessId);
        try
        {
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(result.Accepted);
            Assert.Equal("Agent.SafetyTimedOut", result.FailureCode);
            Assert.True(await WaitUntilProcessExitsAsync(child));
        }
        finally
        {
            await TerminateProcessTreeAsync(child);
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
        var childProcessId = await WaitForProcessIdAsync(childProcessIdFile);
        using var child = OpenRunningProcess(childProcessId);
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
            await TerminateProcessTreeAsync(child);
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
        Process? safetyRoot = null;
        Process? safetyChild = null;
        try
        {
            var safetyRootProcessId = await WaitForProcessIdAsync(
                Path.Combine(_root, "safety-root.pid"));
            var safetyChildProcessId = await WaitForProcessIdAsync(
                Path.Combine(_root, "safety-child.pid"));
            safetyRoot = OpenRunningProcess(safetyRootProcessId);
            safetyChild = OpenRunningProcess(safetyChildProcessId);

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
                    owner.Kill(entireProcessTree: true);
                    await owner.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (InvalidOperationException)
            {
            }

            if (safetyRoot is not null)
            {
                await TerminateProcessTreeAsync(safetyRoot);
                safetyRoot.Dispose();
            }
            if (safetyChild is not null)
            {
                await TerminateProcessTreeAsync(safetyChild);
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

    private static async Task<int> WaitForProcessIdAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    return await ReadProcessIdAsync(path);
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
            $"Safety actuator child PID was not published: {path}");
    }

    private static async Task<int> ReadProcessIdAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        return int.TryParse(
            text,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var processId)
            && processId > 0
                ? processId
                : throw new FormatException(
                    $"Safety actuator child PID is invalid: {text}");
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

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (InvalidOperationException)
        {
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

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
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
}
