using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Execution;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.StationRuntime.TestHelper;

namespace OpenLineOps.Agent.Tests;

public sealed class ProcessStationRuntimeHostCancellationTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-station-host-{Guid.NewGuid():N}");

    [Fact]
    public async Task AgentCancellationKillsStationRuntimeChildProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pidFile = Path.Combine(_root, "cancel-child.pid");
        var host = CreateHost(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        var execution = host.ExecuteAsync(
                CreateRequest(pidFile),
                static (_, _) => ValueTask.CompletedTask,
                cancellation.Token)
            .AsTask();
        var childProcessId = await WaitForChildProcessIdAsync(pidFile);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await IsProcessRunningAsync(childProcessId));
    }

    [Fact]
    public async Task AgentHostTimeoutKillsStationRuntimeChildProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pidFile = Path.Combine(_root, "timeout-child.pid");
        var host = CreateHost(TimeSpan.FromSeconds(10));
        var execution = host.ExecuteAsync(
                CreateRequest(pidFile),
                static (_, _) => ValueTask.CompletedTask)
            .AsTask();
        var childProcessId = await WaitForChildProcessIdAsync(pidFile);

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(ExecutionStatus.TimedOut, result.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, result.Judgement);
        Assert.Equal("Agent.RuntimeTimedOut", result.FailureCode);
        Assert.False(await IsProcessRunningAsync(childProcessId));
    }

    [Fact]
    public async Task HardAgentHostTerminationClosesJobAndKillsRuntimeAndVendorTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var helperAssembly = typeof(StationRuntimeTestHelperMarker).Assembly.Location;
        var executable = Path.ChangeExtension(helperAssembly, ".exe");
        Assert.True(File.Exists(executable), $"Station runtime test helper apphost is missing: {executable}");
        var childPidFile = Path.Combine(_root, "crash-vendor-child.pid");
        var runtimePidFile = Path.Combine(_root, "crash-runtime.pid");
        var supervisorWork = Path.Combine(_root, "supervisor-work");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("supervise-operation");
        AddOption(startInfo, "pid-file", childPidFile);
        AddOption(startInfo, "runtime-pid-file", runtimePidFile);
        AddOption(startInfo, "work-directory", supervisorWork);
        using var supervisor = Process.Start(startInfo)
                               ?? throw new InvalidOperationException("Agent crash supervisor did not start.");
        var runtimeProcessId = await WaitForChildProcessIdAsync(runtimePidFile);
        var vendorChildProcessId = await WaitForChildProcessIdAsync(childPidFile);
        Assert.True(IsProcessRunning(runtimeProcessId));
        Assert.True(IsProcessRunning(vendorChildProcessId));

        supervisor.Kill();
        await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(await IsProcessRunningAsync(runtimeProcessId));
        Assert.False(await IsProcessRunningAsync(vendorChildProcessId));
    }

    [Fact]
    public async Task AgentRestartDeletesOnlyItsPersistedRecoveryJobProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string profileNamespace = "OpenLineOps.AgentRestartTests";
        var job = CreateRunningJob(Path.Combine(_root, "unused.pid"));
        var targetProfile = StationRuntimeIsolationProfile.CreateName(
            profileNamespace,
            job.AgentId,
            job.StationId,
            job.Id);
        var otherAgentProfile = StationRuntimeIsolationProfile.CreateName(
            profileNamespace,
            "agent-other-station",
            job.StationId,
            job.Id);
        Assert.NotEqual(targetProfile, otherAgentProfile);
        _ = WindowsAppContainerIdentity.EnsureProfile(targetProfile);
        _ = WindowsAppContainerIdentity.EnsureProfile(otherAgentProfile);
        try
        {
            var store = new InMemoryStationJobStore();
            Assert.True(await store.TryAddAsync(job, Guid.NewGuid(), []));
            var host = CreateHost(TimeSpan.FromSeconds(30), profileNamespace);
            var targetWorkDirectory = Path.Combine(
                _root,
                "work",
                $"{job.Id.Value:N}-{Guid.NewGuid():N}");
            var otherWorkDirectory = Path.Combine(
                _root,
                "work",
                $"{Guid.NewGuid():N}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(targetWorkDirectory);
            Directory.CreateDirectory(otherWorkDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(targetWorkDirectory, "orphan.txt"),
                "persisted-running-job");
            await File.WriteAllTextAsync(
                Path.Combine(otherWorkDirectory, "other-job.txt"),
                "must-remain");
            var coordinator = new StationJobCoordinator(
                store,
                new ExecutorMustNotRun(),
                new AcceptingFenceValidator(),
                new EmptyCancellationStore(),
                new StationJobExecutionRegistry(),
                host,
                new FixedClock(Now));

            var recovered = await coordinator.RecoverAsync();

            Assert.Equal(
                StationJobStatus.RecoveryRequired,
                Assert.Single(recovered).Status);
            Assert.False(WindowsAppContainerIdentity.ProfileExists(targetProfile));
            Assert.True(WindowsAppContainerIdentity.ProfileExists(otherAgentProfile));
            Assert.False(Directory.Exists(targetWorkDirectory));
            Assert.True(Directory.Exists(otherWorkDirectory));
        }
        finally
        {
            WindowsAppContainerIdentity.DeleteProfile(targetProfile);
            WindowsAppContainerIdentity.DeleteProfile(otherAgentProfile);
        }
    }

    private ProcessStationRuntimeHost CreateHost(
        TimeSpan timeout,
        string? appContainerProfileNamespace = null)
    {
        Directory.CreateDirectory(_root);
        var helperAssembly = typeof(StationRuntimeTestHelperMarker).Assembly.Location;
        var executable = Path.ChangeExtension(helperAssembly, ".exe");
        Assert.True(File.Exists(executable), $"Station runtime test helper apphost is missing: {executable}");
        return new ProcessStationRuntimeHost(
            new ProcessStationRuntimeHostOptions(
                executable,
                Path.Combine(_root, "work"),
                Path.Combine(_root, "artifacts"),
                timeout,
                RequireExternalProgramAppContainerIsolation:
                    appContainerProfileNamespace is not null,
                ExternalProgramAppContainerProfileNamespace:
                    appContainerProfileNamespace),
            new AcceptingFenceValidator(),
            clock: new FixedClock(Now));
    }

    private StationRuntimeExecutionRequest CreateRequest(string pidFile)
    {
        var job = CreateRunningJob(pidFile);
        var packageDirectory = Path.Combine(_root, "package");
        Directory.CreateDirectory(packageDirectory);
        return new StationRuntimeExecutionRequest(job.ToSnapshot(), packageDirectory);
    }

    private StationJob CreateRunningJob(string pidFile)
    {
        Directory.CreateDirectory(_root);
        var job = StationJob.Request(new StationJobRequest(
            new StationJobId(Guid.NewGuid()),
            $"run/{Guid.NewGuid():N}/operation@1",
            "agent-station",
            "station-main",
            "system-station-main",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new StationOperationRunId("operation-main@1"),
            1,
            "product-board",
            "serialNumber",
            "BOARD-001",
            null,
            null,
            "project-main",
            "application-main",
            "snapshot-main",
            "line-main",
            "topology-main",
            "operator-main",
            new string('a', 64),
            "operation-main",
            "flow-main",
            "flow-main-release",
            "configuration-main",
            "recipe-main",
            [new StationResourceFenceEvidence(
                "Station",
                "station-main",
                1,
                Now.AddHours(1))],
            JsonSerializer.Serialize(new { mode = "spawn-child", pidFile }),
            Now));
        job.Accept(Now);
        job.Start(Now);
        return job;
    }

    private static async Task<int> WaitForChildProcessIdAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    var value = await File.ReadAllTextAsync(path);
                    if (int.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var processId)
                        && processId > 0)
                    {
                        return processId;
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Station runtime child PID was not readable: {path}");
    }

    private static async Task<bool> IsProcessRunningAsync(int processId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }

            await Task.Delay(25);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return true;
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

    private static void AddOption(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add($"--{name}");
        startInfo.ArgumentList.Add(value);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class AcceptingFenceValidator : IStationResourceFenceValidator
    {
        public ValueTask<StationResourceFenceValidationResult> ValidateCurrentAsync(
            StationJobSnapshot job,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StationResourceFenceValidationResult.Accept());
    }

    private sealed class ExecutorMustNotRun : IStationOperationExecutor
    {
        public ValueTask<StationOperationExecutionResult> ExecuteAsync(
            StationJobSnapshot job,
            Func<StationOperationProgress, CancellationToken, ValueTask> reportProgress,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A recovered non-idempotent job must not execute.");
    }

    private sealed class EmptyCancellationStore : IStationSafetyInboxStore
    {
        public ValueTask<StationSafetyInboxEntry?> GetAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StationSafetyInboxEntry?>(null);

        public ValueTask<StationSafetyInboxEntry?> GetJobCancellationAsync(
            StationJobId jobId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StationSafetyInboxEntry?>(null);

        public ValueTask<bool> TryBeginAsync(
            StationSafetyInboxEntry entry,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<StationSafetyInboxEntry> CompleteAsync(
            string idempotencyKey,
            StationSafetyCommandKind commandKind,
            string requestSha256,
            string acknowledgementJson,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
