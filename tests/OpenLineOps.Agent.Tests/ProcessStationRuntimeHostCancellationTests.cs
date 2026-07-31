using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Execution;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.StationRuntime.TestHelper;

namespace OpenLineOps.Agent.Tests;

public sealed class ProcessStationRuntimeHostCancellationTests : IDisposable
{
    private const string RestrictedServiceSid =
        "S-1-5-80-123-456-789-1011-1213";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-station-host-{Guid.NewGuid():N}");

    [Fact]
    public void ConstructorRejectsMissingPythonWorkerPolicy()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ProcessStationRuntimeHost(
            new ProcessStationRuntimeHostOptions(
                Path.Combine(_root, "station-runtime.exe"),
                Path.Combine(_root, "plugin-host.exe"),
                Path.Combine(_root, "work"),
                Path.Combine(_root, "artifacts"),
                TimeSpan.FromMinutes(1)),
            new AcceptingFenceValidator()));

        Assert.Contains("Python script", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsExternalProcessWhenLeastPrivilegeIsRequired()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ProcessStationRuntimeHost(
            new ProcessStationRuntimeHostOptions(
                Path.Combine(_root, "station-runtime.exe"),
                Path.Combine(_root, "plugin-host.exe"),
                Path.Combine(_root, "work"),
                Path.Combine(_root, "artifacts"),
                TimeSpan.FromMinutes(1),
                PythonScript: new StationRuntimePythonScriptOptions(
                    Path.Combine(_root, "script-worker.exe"),
                    Path.Combine(_root, "python312.dll"),
                    new StationRuntimePythonScriptSandboxOptions(
                        RequireLeastPrivilegeExecution: true,
                        IsolationMode: StationRuntimePythonScriptIsolationModes.ExternalProcess))),
            new AcceptingFenceValidator()));

        Assert.Contains("least-privilege", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConstructorRejectsStationAgentContainerIsolation(bool requireLeastPrivilege)
    {
        var sandbox = new StationRuntimePythonScriptSandboxOptions(
            RequireLeastPrivilegeExecution: requireLeastPrivilege,
            IsolationMode: "Container");

        var exception = Assert.Throws<ArgumentException>(() => new ProcessStationRuntimeHost(
            ConstructorOptions(sandbox),
            new AcceptingFenceValidator()));

        Assert.Contains("Unsupported Station Agent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsRelativeLeastPrivilegeLauncher()
    {
        var sandbox = new StationRuntimePythonScriptSandboxOptions(
            RequireLeastPrivilegeExecution: true,
            IsolationMode: StationRuntimePythonScriptIsolationModes.LeastPrivilegeIdentity,
            LeastPrivilegeIdentity: "station-python",
            LeastPrivilegeLauncherExecutable: "launcher.exe");

        var exception = Assert.Throws<ArgumentException>(() => new ProcessStationRuntimeHost(
            ConstructorOptions(sandbox),
            new AcceptingFenceValidator()));

        Assert.Contains("must be absolute", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsMissingLeastPrivilegeLauncher()
    {
        var missingLauncher = Path.Combine(_root, "missing-launcher.exe");
        var sandbox = new StationRuntimePythonScriptSandboxOptions(
            RequireLeastPrivilegeExecution: true,
            IsolationMode: StationRuntimePythonScriptIsolationModes.LeastPrivilegeIdentity,
            LeastPrivilegeIdentity: "station-python",
            LeastPrivilegeLauncherExecutable: missingLauncher);

        var exception = Assert.Throws<FileNotFoundException>(() => new ProcessStationRuntimeHost(
            ConstructorOptions(sandbox),
            new AcceptingFenceValidator()));

        Assert.Equal(Path.GetFullPath(missingLauncher), exception.FileName);
    }

    [Fact]
    public void ConstructorRejectsInteractiveRequiredLeastPrivilegeLauncher()
    {
        var sandbox = new StationRuntimePythonScriptSandboxOptions(
            RequireLeastPrivilegeExecution: true,
            IsolationMode: StationRuntimePythonScriptIsolationModes.LeastPrivilegeIdentity,
            LeastPrivilegeIdentity: "station-python",
            LeastPrivilegeLauncherExecutable: CreatePlaceholder("least-privilege-launcher.exe"),
            LeastPrivilegeNoInteractivePrompt: false);

        var exception = Assert.Throws<ArgumentException>(() => new ProcessStationRuntimeHost(
            ConstructorOptions(sandbox),
            new AcceptingFenceValidator()));

        Assert.Contains("interactive launcher prompts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsCustomRequiredLeastPrivilegeLauncherTemplate()
    {
        var sandbox = new StationRuntimePythonScriptSandboxOptions(
            RequireLeastPrivilegeExecution: true,
            IsolationMode: StationRuntimePythonScriptIsolationModes.LeastPrivilegeIdentity,
            LeastPrivilegeIdentity: "station-python",
            LeastPrivilegeLauncherExecutable: CreatePlaceholder("least-privilege-launcher.exe"),
            LeastPrivilegeArgumentsTemplate: "{ExecutablePath} {Arguments}");

        var exception = Assert.Throws<ArgumentException>(() => new ProcessStationRuntimeHost(
            ConstructorOptions(sandbox),
            new AcceptingFenceValidator()));

        Assert.Contains("custom launcher", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StrictExternalProgramPolicyInjectsExactStationServiceEnvironment()
    {
        var sandbox = new StationRuntimePythonScriptSandboxOptions(
            RequireLeastPrivilegeExecution: false,
            IsolationMode: StationRuntimePythonScriptIsolationModes.ExternalProcess);
        var baseOptions = ConstructorOptions(sandbox);
        var host = new ProcessStationRuntimeHost(
            baseOptions with
            {
                RequireRestrictedExternalProgramHostIdentity = true,
                RestrictedServiceSid = RestrictedServiceSid,
                RequireExternalProgramAppContainerIsolation = true,
                ExternalProgramAppContainerProfileNamespace = "OpenLineOps.Agent.Tests",
                RequireImmutableExternalProgramContent = true
            },
            new AcceptingFenceValidator());

        var environment = host.CreateRuntimeEnvironment(
            Path.Combine(_root, "strict-runtime-work"),
            "OpenLineOps.Agent.Tests.Profile");

        Assert.Equal(
            "true",
            environment[
                "OpenLineOps__Devices__ExternalProgramHost__RequireRestrictedHostIdentity"]);
        Assert.Equal(
            "true",
            environment[
                "OpenLineOps__Devices__ExternalProgramHost__RequireImmutableContentProtection"]);
        Assert.Equal(
            RestrictedServiceSid,
            environment["OpenLineOps__Devices__ExternalProgramHost__RestrictedServiceSid"]);
    }

    [Fact]
    public void ConstructorRejectsImmutableContentWithoutRestrictedStationServiceIdentity()
    {
        var sandbox = new StationRuntimePythonScriptSandboxOptions(
            RequireLeastPrivilegeExecution: false,
            IsolationMode: StationRuntimePythonScriptIsolationModes.ExternalProcess);
        var options = ConstructorOptions(sandbox) with
        {
            RequireImmutableExternalProgramContent = true
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new ProcessStationRuntimeHost(options, new AcceptingFenceValidator()));

        Assert.Contains("exact restricted Station service SID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsRestrictedIdentityWithoutExactStationServiceSid()
    {
        var sandbox = new StationRuntimePythonScriptSandboxOptions(
            RequireLeastPrivilegeExecution: false,
            IsolationMode: StationRuntimePythonScriptIsolationModes.ExternalProcess);
        var options = ConstructorOptions(sandbox) with
        {
            RequireRestrictedExternalProgramHostIdentity = true
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new ProcessStationRuntimeHost(options, new AcceptingFenceValidator()));

        Assert.Contains("one exact Station service SID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsImmutableContentWithoutAppContainerIsolation()
    {
        var sandbox = new StationRuntimePythonScriptSandboxOptions(
            RequireLeastPrivilegeExecution: false,
            IsolationMode: StationRuntimePythonScriptIsolationModes.ExternalProcess);
        var options = ConstructorOptions(sandbox) with
        {
            RequireRestrictedExternalProgramHostIdentity = true,
            RestrictedServiceSid = RestrictedServiceSid,
            RequireImmutableExternalProgramContent = true
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new ProcessStationRuntimeHost(options, new AcceptingFenceValidator()));

        Assert.Contains("requires AppContainer isolation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppContainerProvisioningFailurePreservesNativeDiagnostics()
    {
        string? observedLifecycleManagerSid = null;
        var pidFile = Path.Combine(_root, "provisioning-must-not-launch.pid");
        var host = CreateHost(
            TimeSpan.FromSeconds(30),
            "OpenLineOps.AgentProvisioningTests",
            deleteAppContainerProfile: static (_, _) => false,
            appContainerProfileArtifactsProbe: static _ =>
                new WindowsAppContainerProfileArtifactState(
                    PackageRootExists: false,
                    ProfileDirectoryExists: false,
                    MappingExists: false,
                    MappingChildrenExists: false,
                    StorageExists: false,
                    StorageChildrenExists: false),
            retryDelay: static (_, _) => ValueTask.CompletedTask,
            ensureAppContainerProfile: (_, lifecycleManagerServiceSid) =>
            {
                observedLifecycleManagerSid = lifecycleManagerServiceSid;
                throw new Win32Exception(
                    5,
                    "Synthetic AppContainer provisioning denial.");
            });

        var result = await host.ExecuteAsync(
            CreateRequest(pidFile),
            static (_, _) => ValueTask.CompletedTask);

        Assert.Equal(ExecutionStatus.Failed, result.ExecutionStatus);
        Assert.Equal(ResultJudgement.Unknown, result.Judgement);
        Assert.Equal(
            "Agent.RuntimeIsolationProvisioningFailed",
            result.FailureCode);
        Assert.Contains(
            "nativeErrorCode=5",
            result.FailureReason,
            StringComparison.Ordinal);
        Assert.Contains(
            "hresult=0x80004005",
            result.FailureReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RestrictedServiceSid, observedLifecycleManagerSid);
        Assert.False(File.Exists(pidFile));
    }

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
            var host = CreateHost(
                TimeSpan.FromSeconds(30),
                profileNamespace,
                deleteAppContainerProfile: static (profileName, _) =>
                    WindowsAppContainerIdentity.DeleteProfile(profileName));
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
                TestStationDispatchControlLeaseVerifier.Accepting(),
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

    [Fact]
    public async Task CleanupRetriesUntilTransientFileHandleReleases()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var job = CreateRunningJob(Path.Combine(_root, "unused-cleanup.pid"));
        var workDirectory = Path.Combine(
            _root,
            "work",
            $"{job.Id.Value:N}-{Guid.NewGuid():N}");
        var nestedDirectory = Path.Combine(workDirectory, "external-program-workspaces");
        Directory.CreateDirectory(nestedDirectory);
        var lockedPath = Path.Combine(nestedDirectory, "vendor-output.txt");
        await File.WriteAllTextAsync(lockedPath, "locked briefly after process exit");
        var host = CreateHost(TimeSpan.FromSeconds(30));
        var lockedFile = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        try
        {
            var cleanup = host.CleanupAsync(job.ToSnapshot()).AsTask();
            await Task.Delay(100);
            Assert.False(cleanup.IsCompleted);

            await lockedFile.DisposeAsync();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await lockedFile.DisposeAsync();
        }

        Assert.False(Directory.Exists(workDirectory));
    }

    [Fact]
    public async Task CleanupRetriesTransientAppContainerProfileDeletion()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var host = CreateHost(
            TimeSpan.FromSeconds(30),
            "OpenLineOps.AgentCleanupRetryTests",
            (_, _) =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new Win32Exception(32, "Synthetic profile storage handle.");
                }

                return true;
            },
            appContainerProfileArtifactsProbe: _ =>
                new WindowsAppContainerProfileArtifactState(
                    PackageRootExists: attempts < 3,
                    ProfileDirectoryExists: false,
                    MappingExists: false,
                    MappingChildrenExists: false,
                    StorageExists: false,
                    StorageChildrenExists: false),
            retryDelay: (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });

        await host.CleanupAsync(
            CreateRunningJob(Path.Combine(_root, "unused-profile-retry.pid")).ToSnapshot());

        Assert.Equal(3, attempts);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)],
            delays);
    }

    [Fact]
    public async Task CleanupAcceptsFileNotFoundOnlyAfterProfileArtifactsAreAbsent()
    {
        var deletionAttempts = 0;
        var probeAttempts = 0;
        var delays = new List<TimeSpan>();
        var host = CreateHost(
            TimeSpan.FromSeconds(30),
            "OpenLineOps.AgentCleanupMissingProfileTests",
            (_, _) =>
            {
                deletionAttempts++;
                throw new Win32Exception(
                    2,
                    "Synthetic profile disappeared during lifecycle preparation.");
            },
            appContainerProfileArtifactsProbe: _ =>
            {
                probeAttempts++;
                return new WindowsAppContainerProfileArtifactState(
                    PackageRootExists: probeAttempts == 1,
                    ProfileDirectoryExists: false,
                    MappingExists: false,
                    MappingChildrenExists: false,
                    StorageExists: false,
                    StorageChildrenExists: false);
            },
            retryDelay: (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });

        await host.CleanupAsync(
            CreateRunningJob(
                    Path.Combine(_root, "unused-missing-profile-cleanup.pid"))
                .ToSnapshot());

        Assert.Equal(2, deletionAttempts);
        Assert.Equal(2, probeAttempts);
        Assert.Equal([TimeSpan.FromMilliseconds(50)], delays);
    }

    [Fact]
    public async Task CleanupOfAlreadyRemovedProfileIsIdempotent()
    {
        var deletionAttempts = 0;
        var probeAttempts = 0;
        var delays = new List<TimeSpan>();
        var host = CreateHost(
            TimeSpan.FromSeconds(30),
            "OpenLineOps.AgentCleanupRemovedProfileTests",
            (_, _) =>
            {
                deletionAttempts++;
                throw new Win32Exception(
                    2,
                    "Synthetic profile no longer exists.");
            },
            appContainerProfileArtifactsProbe: _ =>
            {
                probeAttempts++;
                return new WindowsAppContainerProfileArtifactState(
                    PackageRootExists: false,
                    ProfileDirectoryExists: false,
                    MappingExists: false,
                    MappingChildrenExists: false,
                    StorageExists: false,
                    StorageChildrenExists: false);
            },
            retryDelay: (delay, _) =>
            {
                delays.Add(delay);
                return ValueTask.CompletedTask;
            });

        var job = CreateRunningJob(
            Path.Combine(_root, "unused-removed-profile-cleanup.pid"));
        await host.CleanupAsync(job.ToSnapshot());
        await host.CleanupAsync(job.ToSnapshot());

        Assert.Equal(2, deletionAttempts);
        Assert.Equal(2, probeAttempts);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task CleanupPassesExactLifecycleManagerSidToProfileDeletion()
    {
        string? observedLifecycleManagerSid = null;
        var host = CreateHost(
            TimeSpan.FromSeconds(30),
            "OpenLineOps.AgentManagedProfileCleanupTests",
            (_, lifecycleManagerServiceSid) =>
            {
                observedLifecycleManagerSid = lifecycleManagerServiceSid;
                return false;
            },
            appContainerProfileArtifactsProbe: static _ =>
                new WindowsAppContainerProfileArtifactState(
                    PackageRootExists: false,
                    ProfileDirectoryExists: false,
                    MappingExists: false,
                    MappingChildrenExists: false,
                    StorageExists: false,
                    StorageChildrenExists: false),
            restrictedServiceSid: RestrictedServiceSid,
            requireRestrictedExternalProgramHostIdentity: true);

        await host.CleanupAsync(
            CreateRunningJob(
                    Path.Combine(_root, "unused-managed-profile-cleanup.pid"))
                .ToSnapshot());

        Assert.Equal(RestrictedServiceSid, observedLifecycleManagerSid);
    }

    [Fact]
    public async Task CleanupReportsPersistentAppContainerProfileDeletionFailure()
    {
        var attempts = 0;
        var host = CreateHost(
            TimeSpan.FromSeconds(30),
            "OpenLineOps.AgentCleanupFailureTests",
            (_, _) =>
            {
                attempts++;
                throw new Win32Exception(5, "Synthetic persistent profile failure.");
            },
            appContainerProfileArtifactsProbe: static _ =>
                new WindowsAppContainerProfileArtifactState(
                    PackageRootExists: true,
                    ProfileDirectoryExists: false,
                    MappingExists: false,
                    MappingChildrenExists: false,
                    StorageExists: false,
                    StorageChildrenExists: false),
            retryDelay: static (_, _) => ValueTask.CompletedTask);

        var exception = await Assert.ThrowsAsync<StationRuntimeIsolationCleanupException>(
            async () => await host.CleanupAsync(
                CreateRunningJob(Path.Combine(_root, "unused-profile-failure.pid")).ToSnapshot()));

        Assert.Equal(24, attempts);
        var failure = Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains("stage=app-container-profile", failure.Message, StringComparison.Ordinal);
        Assert.Contains("nativeErrorCode=5", failure.Message, StringComparison.Ordinal);
        var deletionFailure = Assert.IsType<IOException>(failure.InnerException);
        Assert.Contains(
            "after 24 bounded attempts",
            deletionFailure.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            5,
            Assert.IsType<Win32Exception>(deletionFailure.InnerException).NativeErrorCode);
    }

    [Fact]
    public async Task CleanupRejectsFalseDeletionWhileAnyProfileArtifactRemains()
    {
        var deletionAttempts = 0;
        var probeAttempts = 0;
        var host = CreateHost(
            TimeSpan.FromSeconds(30),
            "OpenLineOps.AgentCleanupArtifactTests",
            (_, _) =>
            {
                deletionAttempts++;
                return false;
            },
            appContainerProfileArtifactsProbe: _ =>
            {
                probeAttempts++;
                return new WindowsAppContainerProfileArtifactState(
                    PackageRootExists: true,
                    ProfileDirectoryExists: false,
                    MappingExists: false,
                    MappingChildrenExists: false,
                    StorageExists: false,
                    StorageChildrenExists: false);
            },
            retryDelay: static (_, _) => ValueTask.CompletedTask);

        var exception = await Assert.ThrowsAsync<StationRuntimeIsolationCleanupException>(
            async () => await host.CleanupAsync(
                CreateRunningJob(Path.Combine(
                    _root,
                    "unused-profile-artifact.pid")).ToSnapshot()));

        Assert.Equal(24, deletionAttempts);
        Assert.Equal(24, probeAttempts);
        var cleanupFailure = Assert.IsType<IOException>(exception.InnerException);
        var deletionFailure = Assert.IsType<IOException>(cleanupFailure.InnerException);
        Assert.Contains(
            "deletion reported no profile while lifecycle artifacts remain",
            deletionFailure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupRemovesRealProfileWithoutAffectingUnrelatedState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string profileNamespace = "OpenLineOps.AgentProfileReleaseRace";
        var job = CreateRunningJob(Path.Combine(_root, "unused-profile-release-race.pid"));
        var profileName = StationRuntimeIsolationProfile.CreateName(
            profileNamespace,
            job.AgentId,
            job.StationId,
            job.Id);
        var unrelatedProfileName = StationRuntimeIsolationProfile.CreateName(
            profileNamespace,
            "agent-unrelated",
            job.StationId,
            job.Id);
        var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(profileName);
        _ = WindowsAppContainerIdentity.EnsureProfile(unrelatedProfileName);
        var workspace = Path.Combine(_root, "profile-release-race-workspace");
        var unrelatedWorkspace = Path.Combine(_root, "profile-release-race-unrelated");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(unrelatedWorkspace);
        await File.WriteAllTextAsync(
            Path.Combine(unrelatedWorkspace, "must-remain.txt"),
            "unrelated");
        WindowsContentAccessAuthorizer.GrantWorkspaceModify(workspace, appContainerSid);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR")!,
            ["PATH"] = Environment.GetEnvironmentVariable("PATH")!,
            ["LOCALAPPDATA"] = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            ["TEMP"] = workspace,
            ["TMP"] = workspace
        };
        using var launched = new WindowsProcessLauncher().Launch(
            new IsolatedProcessStartRequest(
                Path.Combine(Environment.SystemDirectory, "ping.exe"),
                ["-t", "127.0.0.1"],
                workspace,
                environment,
                new WindowsProcessLimits(
                    ActiveProcessLimit: 2,
                    ProcessMemoryLimitBytes: 128L * 1024 * 1024,
                    JobMemoryLimitBytes: 256L * 1024 * 1024,
                    CpuTimeLimit: TimeSpan.FromMinutes(2)),
                new WindowsAppContainerPolicy(
                    profileName,
                    NetworkAccessAllowed: false,
                    ProfileMode: WindowsAppContainerProfileMode.UseExisting)));
        launched.StandardInput.Dispose();
        try
        {
            Assert.True(launched.ActiveProcessCount > 0);
            var host = CreateHost(
                TimeSpan.FromSeconds(30),
                profileNamespace,
                static (profileName, _) =>
                    WindowsAppContainerIdentity.DeleteProfile(profileName),
                retryDelay: async (delay, cancellationToken) =>
                {
                    launched.TerminateProcessTree();
                    await launched.WaitForExitAsync(cancellationToken);
                    await Task.Delay(delay, cancellationToken);
                });

            await host.CleanupAsync(job.ToSnapshot());

            Assert.False(WindowsAppContainerIdentity.ProfileExists(profileName));
            Assert.True(WindowsAppContainerIdentity.ProfileExists(unrelatedProfileName));
            Assert.True(File.Exists(
                Path.Combine(unrelatedWorkspace, "must-remain.txt")));
        }
        finally
        {
            launched.TerminateProcessTree();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await launched.WaitForExitAsync(timeout.Token);
            _ = WindowsAppContainerIdentity.DeleteProfile(profileName);
            _ = WindowsAppContainerIdentity.DeleteProfile(unrelatedProfileName);
        }
    }

    private ProcessStationRuntimeHost CreateHost(
        TimeSpan timeout,
        string? appContainerProfileNamespace = null,
        Func<string, string?, bool>? deleteAppContainerProfile = null,
        Func<string, WindowsAppContainerProfileArtifactState>?
            appContainerProfileArtifactsProbe = null,
        Func<TimeSpan, CancellationToken, ValueTask>? retryDelay = null,
        Func<string, string?, string>? ensureAppContainerProfile = null,
        string? restrictedServiceSid = null,
        bool requireRestrictedExternalProgramHostIdentity = false)
    {
        Directory.CreateDirectory(_root);
        var helperAssembly = typeof(StationRuntimeTestHelperMarker).Assembly.Location;
        var executable = Path.ChangeExtension(helperAssembly, ".exe");
        Assert.True(File.Exists(executable), $"Station runtime test helper apphost is missing: {executable}");
        var requiresAppContainer = appContainerProfileNamespace is not null;
        var effectiveRestrictedIdentity =
            requireRestrictedExternalProgramHostIdentity || requiresAppContainer;
        var effectiveRestrictedServiceSid = restrictedServiceSid
                                            ?? (requiresAppContainer
                                                ? RestrictedServiceSid
                                                : null);
        return new ProcessStationRuntimeHost(
            new ProcessStationRuntimeHostOptions(
                executable,
                PluginHostExecutablePath(),
                Path.Combine(_root, "work"),
                Path.Combine(_root, "artifacts"),
                timeout,
                RestrictedServiceSid: effectiveRestrictedServiceSid,
                RequireRestrictedExternalProgramHostIdentity:
                    effectiveRestrictedIdentity,
                RequireExternalProgramAppContainerIsolation:
                    requiresAppContainer,
                ExternalProgramAppContainerProfileNamespace:
                    appContainerProfileNamespace,
                PythonScript: PythonScriptOptions()),
            new AcceptingFenceValidator(),
            processLauncher: null,
            clock: new FixedClock(Now),
            ensureAppContainerProfile:
                ensureAppContainerProfile
                ?? (static (profileName, profileLifecycleManagerServiceSid) =>
                    WindowsAppContainerIdentity.EnsureProfile(
                        profileName,
                        profileLifecycleManagerServiceSid)),
            deleteAppContainerProfile:
                deleteAppContainerProfile
                ?? (static (profileName, profileLifecycleManagerServiceSid) =>
                    WindowsAppContainerIdentity.DeleteProfile(
                        profileName,
                        profileLifecycleManagerServiceSid)),
            appContainerProfileArtifactsProbe:
                appContainerProfileArtifactsProbe
                ?? WindowsAppContainerIdentity.ProbeProfileArtifacts,
            retryDelay: retryDelay ?? (static (delay, cancellationToken) =>
                new ValueTask(Task.Delay(delay, cancellationToken))));
    }

    private ProcessStationRuntimeHostOptions ConstructorOptions(
        StationRuntimePythonScriptSandboxOptions sandbox) => new(
        CreatePlaceholder("station-runtime.exe"),
        CreatePlaceholder("plugin-host.exe"),
        Path.Combine(_root, "constructor-work"),
        Path.Combine(_root, "constructor-artifacts"),
        TimeSpan.FromMinutes(1),
        PythonScript: new StationRuntimePythonScriptOptions(
            CreatePlaceholder("script-worker.exe"),
            CreatePlaceholder("python312.dll"),
            sandbox));

    private string CreatePlaceholder(string fileName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, []);
        }

        return path;
    }

    private static StationRuntimePythonScriptOptions PythonScriptOptions()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var pythonRuntime = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
        if (string.IsNullOrWhiteSpace(pythonRuntime) || !File.Exists(pythonRuntime))
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "-c \"import pathlib,sysconfig; print(pathlib.Path(sysconfig.get_config_var('BINDIR')).joinpath(sysconfig.get_config_var('LDLIBRARY')).resolve())\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Python runtime discovery process did not start.");
            if (!process.WaitForExit(2_000) || process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Station Runtime host tests could not discover the Python runtime DLL.");
            }

            pythonRuntime = process.StandardOutput.ReadLine();
            if (string.IsNullOrWhiteSpace(pythonRuntime) || !File.Exists(pythonRuntime))
            {
                throw new InvalidOperationException(
                    "Station Runtime host tests require an installed Python runtime DLL.");
            }
        }

        return new StationRuntimePythonScriptOptions(
            Path.Combine(
                RepositoryRoot(),
                "src",
                "OpenLineOps.ScriptWorker",
                "bin",
                configuration,
                "net10.0",
                "OpenLineOps.ScriptWorker.exe"),
            Path.GetFullPath(pythonRuntime),
            new StationRuntimePythonScriptSandboxOptions(
                RequireLeastPrivilegeExecution: false,
                IsolationMode: StationRuntimePythonScriptIsolationModes.ExternalProcess));
    }

    private static string PluginHostExecutablePath()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        return Path.Combine(
            RepositoryRoot(),
            "src",
            "OpenLineOps.PluginHost",
            "bin",
            configuration,
            "net10.0",
            "OpenLineOps.PluginHost.exe");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "OpenLineOps.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException(
                   "OpenLineOps repository root could not be found.");
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
            ProductionContextDocument.Write(new Dictionary<string, ProductionContextValue>(
                StringComparer.Ordinal)
            {
                ["mode"] = new(ProductionContextValueKind.Text, "spawn-child"),
                ["pidFile"] = new(ProductionContextValueKind.Text, pidFile)
            }).GetRawText(),
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
