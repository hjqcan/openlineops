using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.VendorTestHelper;

namespace OpenLineOps.ProcessIsolation.Tests;

public sealed class WindowsProcessLauncherTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ExitCodeComesFromTheOwnedCreateProcessHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var launched = Launch(
            [
                "sandbox-exit",
                OpenLineOps.VendorTestHelper.Program.CrashExitCode.ToString(
                    CultureInfo.InvariantCulture)
            ],
            EnvironmentForChild());
        launched.StandardInput.Dispose();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        await launched.WaitForExitAsync(timeout.Token);

        Assert.Equal(OpenLineOps.VendorTestHelper.Program.CrashExitCode, launched.ExitCode);
        await WaitForJobEmptyAsync(launched, timeout.Token);
        Assert.Equal(0u, launched.ActiveProcessCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("contains spaces")]
    [InlineData("contains\"quote")]
    [InlineData("ends with slash \\")]
    [InlineData("混合 Unicode 参数 \\\"")]
    public async Task LaunchPreservesEveryWindowsArgument(string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var expected = new[]
        {
            value,
            string.Empty,
            "a b",
            "quoted \" value",
            @"C:\path with spaces\",
            @"three\\\",
            "中文"
        };
        using var launched = Launch(
            ["sandbox-observe", .. expected],
            EnvironmentForChild());
        launched.StandardInput.Dispose();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        var stdout = ReadUtf8Async(launched.StandardOutput, timeout.Token);
        var stderr = ReadUtf8Async(launched.StandardError, timeout.Token);
        await launched.WaitForExitAsync(timeout.Token);
        var observation = JsonSerializer.Deserialize<SandboxObservation>(
            await stdout,
            JsonOptions())!;
        Assert.Equal(0, launched.ExitCode);
        Assert.Equal(string.Empty, await stderr);
        Assert.Equal(expected, observation.Arguments);
    }

    [Fact]
    public async Task LaunchUsesOnlyTheExactEnvironmentBlock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string inheritedSecretName = "OPENLINEOPS_SANDBOX_SECRET_MUST_NOT_LEAK";
        var previous = Environment.GetEnvironmentVariable(inheritedSecretName);
        Environment.SetEnvironmentVariable(inheritedSecretName, "secret");
        try
        {
            var environment = EnvironmentForChild();
            environment.Add("OPENLINEOPS_ALLOWED_VALUE", "允许值");
            using var launched = Launch(["sandbox-observe"], environment);
            launched.StandardInput.Dispose();
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var stdout = ReadUtf8Async(launched.StandardOutput, timeout.Token);
            var stderr = ReadUtf8Async(launched.StandardError, timeout.Token);
            await launched.WaitForExitAsync(timeout.Token);
            var observation = JsonSerializer.Deserialize<SandboxObservation>(
                await stdout,
                JsonOptions())!;
            Assert.Equal(0, launched.ExitCode);
            Assert.Equal(string.Empty, await stderr);
            Assert.Equal("允许值", observation.Environment["OPENLINEOPS_ALLOWED_VALUE"]);
            Assert.False(observation.Environment.ContainsKey(inheritedSecretName));
            Assert.All(
                observation.Environment.Keys,
                key => Assert.Contains(key, environment.Keys, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable(inheritedSecretName, previous);
        }
    }

    [Fact]
    public async Task LaunchCreatesARealAppContainerTokenWithoutLeakingHostEnvironment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string profileName = "OpenLineOps.Tests.ProcessIsolation";
        var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(profileName);
        var executableDirectory = NewPath("appcontainer-helper");
        var executable = CopyHelperPayload(executableDirectory);
        var workspace = NewPath("appcontainer-workspace");
        Directory.CreateDirectory(workspace);
        WindowsContentAccessAuthorizer.GrantReadExecute(executableDirectory, appContainerSid);
        WindowsContentAccessAuthorizer.GrantWorkspaceModify(workspace, appContainerSid);

        const string inheritedSecretName = "OPENLINEOPS_APPCONTAINER_SECRET_MUST_NOT_LEAK";
        var previous = Environment.GetEnvironmentVariable(inheritedSecretName);
        Environment.SetEnvironmentVariable(inheritedSecretName, "secret");
        try
        {
            var environment = EnvironmentForChild();
            environment.Add(
                "LOCALAPPDATA",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            using var launched = new WindowsProcessLauncher().Launch(
                new IsolatedProcessStartRequest(
                    executable,
                    ["sandbox-observe"],
                    workspace,
                    environment,
                    new WindowsProcessLimits(
                        ActiveProcessLimit: 4,
                        ProcessMemoryLimitBytes: 512L * 1024 * 1024,
                        JobMemoryLimitBytes: 1024L * 1024 * 1024,
                        CpuTimeLimit: TimeSpan.FromMinutes(5)),
                    new WindowsAppContainerPolicy(profileName, NetworkAccessAllowed: false)));
            launched.StandardInput.Dispose();
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var stdout = ReadUtf8Async(launched.StandardOutput, timeout.Token);
            var stderr = ReadUtf8Async(launched.StandardError, timeout.Token);
            await launched.WaitForExitAsync(timeout.Token);
            var observation = JsonSerializer.Deserialize<AppContainerObservation>(
                await stdout,
                JsonOptions())!;

            Assert.Equal(0, launched.ExitCode);
            Assert.Equal(string.Empty, await stderr);
            Assert.True(observation.IsAppContainer);
            Assert.False(observation.HasInternetClientCapability);
            Assert.False(observation.Environment.ContainsKey(inheritedSecretName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(inheritedSecretName, previous);
            Directory.Delete(executableDirectory, recursive: true);
            Directory.Delete(workspace, recursive: true);
            WindowsAppContainerIdentity.DeleteProfile(profileName);
        }
    }

    [Fact]
    public async Task AppContainerWithNetworkPermissionReceivesOnlyInternetClientCapability()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string profileName = "OpenLineOps.Tests.ProcessIsolation";
        var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(profileName);
        var executableDirectory = NewPath("appcontainer-network-helper");
        var executable = CopyHelperPayload(executableDirectory);
        var workspace = NewPath("appcontainer-network-capability-workspace");
        Directory.CreateDirectory(workspace);
        WindowsContentAccessAuthorizer.GrantReadExecute(executableDirectory, appContainerSid);
        WindowsContentAccessAuthorizer.GrantWorkspaceModify(workspace, appContainerSid);
        try
        {
            var environment = EnvironmentForChild();
            environment.Add(
                "LOCALAPPDATA",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            using var launched = new WindowsProcessLauncher().Launch(
                new IsolatedProcessStartRequest(
                    executable,
                    ["sandbox-observe"],
                    workspace,
                    environment,
                    new WindowsProcessLimits(
                        ActiveProcessLimit: 4,
                        ProcessMemoryLimitBytes: 512L * 1024 * 1024,
                        JobMemoryLimitBytes: 1024L * 1024 * 1024,
                        CpuTimeLimit: TimeSpan.FromMinutes(5)),
                    new WindowsAppContainerPolicy(profileName, NetworkAccessAllowed: true)));
            launched.StandardInput.Dispose();
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var stdout = ReadUtf8Async(launched.StandardOutput, timeout.Token);
            var stderr = ReadUtf8Async(launched.StandardError, timeout.Token);
            await launched.WaitForExitAsync(timeout.Token);
            var observation = JsonSerializer.Deserialize<AppContainerObservation>(
                await stdout,
                JsonOptions())!;

            Assert.Equal(0, launched.ExitCode);
            Assert.Equal(string.Empty, await stderr);
            Assert.True(observation.IsAppContainer);
            Assert.True(observation.HasInternetClientCapability);
        }
        finally
        {
            Directory.Delete(executableDirectory, recursive: true);
            Directory.Delete(workspace, recursive: true);
            WindowsAppContainerIdentity.DeleteProfile(profileName);
        }
    }

    [Fact]
    public async Task AppContainerExecutesFromProtectedContentWithDualPrincipalAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string profileName = "OpenLineOps.Tests.ProcessIsolation";
        var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(profileName);
        var contentCapabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        using var currentIdentity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var hostReaderSid = currentIdentity.User?.Value
                            ?? throw new InvalidOperationException("Current process identity has no SID.");
        var sourceExecutable = HelperExecutablePath();
        var sourceDirectory = Path.GetDirectoryName(sourceExecutable)!;
        var cacheRoot = NewPath("protected-cache");
        var contentDirectory = Path.Combine(cacheRoot, new string('b', 64));
        var workspace = NewPath("protected-appcontainer-workspace");
        Directory.CreateDirectory(contentDirectory);
        Directory.CreateDirectory(workspace);
        var inventory = new List<ImmutableContentFile>();
        foreach (var extension in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var fileName = "OpenLineOps.VendorTestHelper" + extension;
            var source = Path.Combine(sourceDirectory, fileName);
            var destination = Path.Combine(contentDirectory, fileName);
            File.Copy(source, destination);
            var bytes = await File.ReadAllBytesAsync(destination);
            inventory.Add(new ImmutableContentFile(
                fileName,
                bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        var probeFiles = Enumerable.Range(1, 5)
            .Select(index => Path.Combine(contentDirectory, $"mutation-probe-{index}.txt"))
            .ToArray();
        foreach (var probeFile in probeFiles)
        {
            var bytes = Encoding.UTF8.GetBytes(Path.GetFileName(probeFile));
            await File.WriteAllBytesAsync(probeFile, bytes);
            inventory.Add(new ImmutableContentFile(
                Path.GetFileName(probeFile),
                bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        var protector = new ImmutableContentProtector();
        await protector.ProtectAsync(
            contentDirectory,
            inventory,
            new ImmutableContentProtectionPolicy(contentCapabilitySid, hostReaderSid));
        WindowsContentAccessAuthorizer.GrantWorkspaceModify(workspace, appContainerSid);
        try
        {
            var environment = EnvironmentForChild();
            environment.Add(
                "LOCALAPPDATA",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            using var launched = new WindowsProcessLauncher().Launch(
                new IsolatedProcessStartRequest(
                    Path.Combine(contentDirectory, "OpenLineOps.VendorTestHelper.exe"),
                    [
                        "sandbox-probe-immutable-content",
                        contentCapabilitySid,
                        .. probeFiles
                    ],
                    workspace,
                    environment,
                    new WindowsProcessLimits(
                        ActiveProcessLimit: 4,
                        ProcessMemoryLimitBytes: 512L * 1024 * 1024,
                        JobMemoryLimitBytes: 1024L * 1024 * 1024,
                        CpuTimeLimit: TimeSpan.FromMinutes(5)),
                    new WindowsAppContainerPolicy(
                        profileName,
                        NetworkAccessAllowed: false,
                        [WindowsAppContainerIdentity.ExternalProgramContentCapabilityName])));
            launched.StandardInput.Dispose();
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var stdout = ReadUtf8Async(launched.StandardOutput, timeout.Token);
            var stderr = ReadUtf8Async(launched.StandardError, timeout.Token);
            await launched.WaitForExitAsync(timeout.Token);
            var observation = JsonSerializer.Deserialize<ImmutableContentMutationObservation>(
                await stdout,
                JsonOptions())!;

            Assert.Equal(0, launched.ExitCode);
            Assert.Equal(string.Empty, await stderr);
            Assert.True(observation.IsAppContainer);
            Assert.True(observation.HasExpectedContentCapability);
            Assert.False(observation.WriteSucceeded);
            Assert.False(observation.RenameSucceeded);
            Assert.False(observation.DeleteSucceeded);
            Assert.False(observation.ChangePermissionsSucceeded);
            Assert.False(observation.TakeOwnershipSucceeded);
        }
        finally
        {
            protector.DeleteProtectedInstallation(cacheRoot, contentDirectory);
            Directory.Delete(cacheRoot);
            Directory.Delete(workspace, recursive: true);
            WindowsAppContainerIdentity.DeleteProfile(profileName);
        }
    }

    [Fact]
    public async Task LongProtectedApplicationPathWithSpacesLaunchesWithExactArguments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileName = "OpenLineOps.Tests.LongPath." + Guid.NewGuid().ToString("N");
        var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(profileName);
        var contentCapabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        using var currentIdentity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var hostReaderSid = currentIdentity.User?.Value
                            ?? throw new InvalidOperationException("Current process identity has no SID.");
        var sourceExecutable = HelperExecutablePath();
        var sourceDirectory = Path.GetDirectoryName(sourceExecutable)!;
        var longRoot = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps long process path " + Guid.NewGuid().ToString("N"),
            "segment one " + new string('a', 48),
            "segment two " + new string('b', 48),
            "segment three " + new string('c', 48));
        var cacheRoot = Path.Combine(longRoot, "protected cache");
        var contentDirectory = Path.Combine(cacheRoot, new string('d', 64));
        var workspace = NewPath("writable workspace with spaces");
        Directory.CreateDirectory(contentDirectory);
        Directory.CreateDirectory(workspace);
        var inventory = new List<ImmutableContentFile>();
        foreach (var extension in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var fileName = "OpenLineOps.VendorTestHelper" + extension;
            var source = Path.Combine(sourceDirectory, fileName);
            var destination = Path.Combine(contentDirectory, fileName);
            File.Copy(source, destination);
            var bytes = await File.ReadAllBytesAsync(destination);
            inventory.Add(new ImmutableContentFile(
                fileName,
                bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        var executable = Path.Combine(contentDirectory, "OpenLineOps.VendorTestHelper.exe");
        Assert.True(executable.Length > 260);
        var protector = new ImmutableContentProtector();
        await protector.ProtectAsync(
            contentDirectory,
            inventory,
            new ImmutableContentProtectionPolicy(contentCapabilitySid, hostReaderSid));
        WindowsContentAccessAuthorizer.GrantWorkspaceModify(workspace, appContainerSid);
        try
        {
            var environment = EnvironmentForChild();
            environment.Add(
                "LOCALAPPDATA",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            using (var launched = new WindowsProcessLauncher().Launch(
                       new IsolatedProcessStartRequest(
                           executable,
                           ["sandbox-observe", "argument with spaces", "中文参数"],
                           workspace,
                           environment,
                           new WindowsProcessLimits(
                               ActiveProcessLimit: 4,
                               ProcessMemoryLimitBytes: 512L * 1024 * 1024,
                               JobMemoryLimitBytes: 1024L * 1024 * 1024,
                               CpuTimeLimit: TimeSpan.FromMinutes(5)),
                           new WindowsAppContainerPolicy(
                               profileName,
                               NetworkAccessAllowed: false,
                               [WindowsAppContainerIdentity.ExternalProgramContentCapabilityName]))))
            {
                launched.StandardInput.Dispose();
                using var timeout = new CancellationTokenSource(ProcessTimeout);
                var stdout = ReadUtf8Async(launched.StandardOutput, timeout.Token);
                var stderr = ReadUtf8Async(launched.StandardError, timeout.Token);
                await launched.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, launched.ExitCode);
                Assert.Equal(string.Empty, await stderr);
                var observation = JsonSerializer.Deserialize<AppContainerObservation>(
                    await stdout,
                    JsonOptions());
                Assert.NotNull(observation);
                Assert.True(observation.IsAppContainer);
                Assert.Equal(["argument with spaces", "中文参数"], observation.Arguments);
            }
        }
        finally
        {
            protector.DeleteProtectedInstallation(cacheRoot, contentDirectory);
            Directory.Delete(cacheRoot);
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(longRoot, recursive: true);
            WindowsAppContainerIdentity.DeleteProfile(profileName);
        }
    }

    [Fact]
    public void LongWorkingDirectoryIsRejectedBeforeNativeLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var longWorkingDirectory = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps long working directory " + Guid.NewGuid().ToString("N"),
            "segment one " + new string('a', 72),
            "segment two " + new string('b', 72),
            "segment three " + new string('c', 72));
        Directory.CreateDirectory(longWorkingDirectory);
        try
        {
            Assert.True(longWorkingDirectory.Length >= 260);
            var exception = Assert.Throws<ArgumentException>(() =>
                new WindowsProcessLauncher().Launch(new IsolatedProcessStartRequest(
                    HelperExecutablePath(),
                    ["sandbox-observe"],
                    longWorkingDirectory,
                    EnvironmentForChild(),
                    new WindowsProcessLimits(
                        ActiveProcessLimit: 4,
                        ProcessMemoryLimitBytes: 512L * 1024 * 1024,
                        JobMemoryLimitBytes: 1024L * 1024 * 1024,
                        CpuTimeLimit: TimeSpan.FromMinutes(5)))));
            Assert.Contains("shorter than 260", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(longWorkingDirectory, recursive: true);
        }
    }

    [Fact]
    public void DeletingEphemeralAppContainerRemovesItsWritableProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileName = "OpenLineOps.Tests.Delete." + Guid.NewGuid().ToString("N");
        var sid = WindowsAppContainerIdentity.EnsureProfile(profileName);
        var profilePath = WindowsAppContainerIdentity.GetProfileFolderPath(sid);
        Assert.True(Directory.Exists(profilePath));

        WindowsAppContainerIdentity.DeleteProfile(profileName);

        Assert.False(Directory.Exists(profilePath));
    }

    [Fact]
    public async Task AppContainerWithoutNetworkCapabilityCannotConnectToListeningSocket()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string profileName = "OpenLineOps.Tests.ProcessIsolation";
        var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(profileName);
        var executableDirectory = NewPath("appcontainer-network-denied-helper");
        var executable = CopyHelperPayload(executableDirectory);
        var workspace = NewPath("appcontainer-network-workspace");
        Directory.CreateDirectory(workspace);
        WindowsContentAccessAuthorizer.GrantReadExecute(executableDirectory, appContainerSid);
        WindowsContentAccessAuthorizer.GrantWorkspaceModify(workspace, appContainerSid);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var environment = EnvironmentForChild();
            environment.Add(
                "LOCALAPPDATA",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            using var launched = new WindowsProcessLauncher().Launch(
                new IsolatedProcessStartRequest(
                    executable,
                    [
                        "sandbox-connect",
                        IPAddress.Loopback.ToString(),
                        port.ToString(CultureInfo.InvariantCulture)
                    ],
                    workspace,
                    environment,
                    new WindowsProcessLimits(
                        ActiveProcessLimit: 4,
                        ProcessMemoryLimitBytes: 512L * 1024 * 1024,
                        JobMemoryLimitBytes: 1024L * 1024 * 1024,
                        CpuTimeLimit: TimeSpan.FromMinutes(5)),
                    new WindowsAppContainerPolicy(profileName, NetworkAccessAllowed: false)));
            launched.StandardInput.Dispose();
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var stdout = ReadUtf8Async(launched.StandardOutput, timeout.Token);
            var stderr = ReadUtf8Async(launched.StandardError, timeout.Token);
            await launched.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, launched.ExitCode);
            Assert.Equal(string.Empty, await stderr);
            Assert.False(JsonSerializer.Deserialize<bool>(await stdout));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(executableDirectory, recursive: true);
            Directory.Delete(workspace, recursive: true);
            WindowsAppContainerIdentity.DeleteProfile(profileName);
        }
    }

    [Fact]
    public async Task LaunchDoesNotInheritUnlistedInheritableHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var attributes = new SecurityAttributes
        {
            Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
            InheritHandle = 1
        };
        var handles = new SafeWaitHandle[64];
        for (var index = 0; index < handles.Length; index++)
        {
            handles[index] = CreateEvent(
                ref attributes,
                manualReset: false,
                initialState: false,
                null);
        }
        try
        {
            Assert.All(handles, handle => Assert.False(handle.IsInvalid));
            var values = handles
                .TakeLast(8)
                .Select(handle => handle.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture))
                .ToArray();
            using var launched = Launch(
                ["sandbox-check-handles", .. values],
                EnvironmentForChild());
            launched.StandardInput.Dispose();
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            var stdout = ReadUtf8Async(launched.StandardOutput, timeout.Token);
            var stderr = ReadUtf8Async(launched.StandardError, timeout.Token);
            await launched.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, launched.ExitCode);
            Assert.Equal(string.Empty, await stderr);
            Assert.All(JsonSerializer.Deserialize<bool[]>(await stdout)!, Assert.False);
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    [Fact]
    public async Task LaunchFailureTerminatesTheSuspendedProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var failureCheckpoint in new[]
                 {
                     WindowsProcessLaunchCheckpoint.ProcessCreated,
                     WindowsProcessLaunchCheckpoint.ProcessAssignedToJob
                 })
        {
            var processId = 0;
            var launcher = new WindowsProcessLauncher((checkpoint, id) =>
            {
                processId = id;
                if (checkpoint == failureCheckpoint)
                {
                    throw new LaunchCheckpointException();
                }
            });
            Assert.Throws<LaunchCheckpointException>(() => Launch(
                ["sandbox-child-wait", NewPath("unused-pid"), "60000"],
                EnvironmentForChild(),
                launcher));
            Assert.NotEqual(0, processId);
            await AssertProcessExitedAsync(processId);
        }
    }

    [Fact]
    public async Task ClosingJobKillsImmediateExitChildrenWithoutLeakingHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int repetitions = 50;
        var hostProcess = Process.GetCurrentProcess();
        var startingHandles = hostProcess.HandleCount;
        for (var iteration = 0; iteration < repetitions; iteration++)
        {
            var pidFile = NewPath($"child-{iteration.ToString(CultureInfo.InvariantCulture)}.pid");
            int childProcessId;
            using (var launched = Launch(
                       ["sandbox-spawn-child-and-exit", pidFile, "60000"],
                       EnvironmentForChild()))
            {
                launched.StandardInput.Dispose();
                using var timeout = new CancellationTokenSource(ProcessTimeout);
                await launched.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, launched.ExitCode);
                childProcessId = await ReadProcessIdAsync(pidFile, timeout.Token);
                Assert.True(IsProcessRunning(childProcessId));
            }

            await AssertProcessExitedAsync(childProcessId);
            File.Delete(pidFile);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        hostProcess.Refresh();
        var handleGrowth = hostProcess.HandleCount - startingHandles;
        Assert.True(handleGrowth <= 2, $"Process handle count grew by {handleGrowth}.");
    }

    [Fact]
    public async Task CancellationClosesJobAndKillsTheFullProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pidFile = NewPath("cancel-child.pid");
        int childProcessId;
        using (var launched = Launch(
                   ["sandbox-spawn-child-and-exit", pidFile, "60000"],
                   EnvironmentForChild()))
        {
            launched.StandardInput.Dispose();
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            await launched.WaitForExitAsync(timeout.Token);
            childProcessId = await ReadProcessIdAsync(pidFile, timeout.Token);
            Assert.True(IsProcessRunning(childProcessId));
            Assert.True(launched.ActiveProcessCount >= 1);
            launched.TerminateProcessTree();
            var stopwatch = Stopwatch.StartNew();
            while (launched.ActiveProcessCount != 0 && stopwatch.Elapsed < ProcessTimeout)
            {
                await Task.Delay(25);
            }

            Assert.Equal(0u, launched.ActiveProcessCount);
        }

        await AssertProcessExitedAsync(childProcessId);
        File.Delete(pidFile);
    }

    private static WindowsIsolatedProcess Launch(
        IReadOnlyCollection<string> helperArguments,
        IReadOnlyDictionary<string, string> environment,
        WindowsProcessLauncher? launcher = null)
    {
        var executable = HelperExecutablePath();
        return (launcher ?? new WindowsProcessLauncher()).Launch(
            new IsolatedProcessStartRequest(
                executable,
                helperArguments,
                Path.GetDirectoryName(executable)!,
                environment,
                new WindowsProcessLimits(
                    ActiveProcessLimit: 4,
                    ProcessMemoryLimitBytes: 512L * 1024 * 1024,
                    JobMemoryLimitBytes: 1024L * 1024 * 1024,
                    CpuTimeLimit: TimeSpan.FromMinutes(5))));
    }

    private static Dictionary<string, string> EnvironmentForChild()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopyEnvironment("SystemRoot", result);
        CopyEnvironment("WINDIR", result);
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        result.Add("TEMP", temp);
        result.Add("TMP", temp);
        return result;
    }

    private static void CopyEnvironment(string name, Dictionary<string, string> target)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
        {
            target[name] = value;
        }
    }

    private static string HelperExecutablePath()
    {
        var assemblyPath = typeof(VendorTestHelperMarker).Assembly.Location;
        var executable = Path.ChangeExtension(assemblyPath, ".exe");
        if (File.Exists(executable))
        {
            return Path.GetFullPath(executable);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenLineOps.slnx")))
        {
            directory = directory.Parent;
        }

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        executable = directory is null
            ? executable
            : Path.Combine(
                directory.FullName,
                "tools",
                "OpenLineOps.VendorTestHelper",
                "bin",
                configuration,
                "net10.0",
                "OpenLineOps.VendorTestHelper.exe");
        return File.Exists(executable)
            ? Path.GetFullPath(executable)
            : throw new FileNotFoundException(
                "Vendor test helper apphost is required for Windows process sandbox tests.",
                executable);
    }

    private static string CopyHelperPayload(string destinationDirectory)
    {
        var sourceExecutable = HelperExecutablePath();
        var sourceDirectory = Path.GetDirectoryName(sourceExecutable)
                              ?? throw new InvalidDataException(
                                  "Vendor test helper executable has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        foreach (var extension in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var fileName = "OpenLineOps.VendorTestHelper" + extension;
            var sourcePath = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    "Vendor test helper payload is incomplete.",
                    sourcePath);
            }
            File.Copy(sourcePath, Path.Combine(destinationDirectory, fileName));
        }

        return Path.Combine(destinationDirectory, "OpenLineOps.VendorTestHelper.exe");
    }

    private static string NewPath(string fileName) =>
        Path.Combine(Path.GetTempPath(), $"openlineops-{Guid.NewGuid():N}-{fileName}");

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
                var text = await File.ReadAllTextAsync(path, cancellationToken);
                return int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            catch (IOException)
            {
                await Task.Delay(10, cancellationToken);
            }
        }
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < ProcessTimeout && IsProcessRunning(processId))
        {
            await Task.Delay(20);
        }

        Assert.False(IsProcessRunning(processId));
    }

    private static async Task WaitForJobEmptyAsync(
        WindowsIsolatedProcess launched,
        CancellationToken cancellationToken)
    {
        while (launched.ActiveProcessCount != 0)
        {
            await Task.Delay(10, cancellationToken);
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

    private static async Task<string> ReadUtf8Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    private sealed record SandboxObservation(
        IReadOnlyCollection<string> Arguments,
        IReadOnlyDictionary<string, string> Environment);

    private sealed record AppContainerObservation(
        IReadOnlyCollection<string> Arguments,
        IReadOnlyDictionary<string, string> Environment,
        bool IsAppContainer,
        bool HasInternetClientCapability);

    private sealed record ImmutableContentMutationObservation(
        bool IsAppContainer,
        bool HasExpectedContentCapability,
        bool WriteSucceeded,
        bool RenameSucceeded,
        bool DeleteSucceeded,
        bool ChangePermissionsSucceeded,
        bool TakeOwnershipSucceeded);

    private sealed class LaunchCheckpointException : Exception;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEvent(
        ref SecurityAttributes eventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name);
}
