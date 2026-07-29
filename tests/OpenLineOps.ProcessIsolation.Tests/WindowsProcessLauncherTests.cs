using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.VendorTestHelper;
using OpenLineOps.WindowsSecurity;

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
        if (!OperatingSystem.IsWindows()
            || !TryGetStationServiceIdentity(out var stationIdentity))
        {
            return;
        }

        const string profileName = "OpenLineOps.Tests.ProcessIsolation";
        var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(profileName);
        var contentCapabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
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
        var protectionPolicy = new ImmutableContentProtectionPolicy(
            contentCapabilitySid,
            stationIdentity.ServiceSid);
        await protector.ProtectAsync(
            contentDirectory,
            inventory,
            protectionPolicy);
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
            BestEffortDeleteTestTree(contentDirectory);
            if (Directory.Exists(cacheRoot)
                && !Directory.EnumerateFileSystemEntries(cacheRoot).Any())
            {
                Directory.Delete(cacheRoot);
            }
            Directory.Delete(workspace, recursive: true);
            WindowsAppContainerIdentity.DeleteProfile(profileName);
        }
    }

    [Fact]
    public async Task LongProtectedApplicationPathWithSpacesLaunchesWithExactArguments()
    {
        if (!OperatingSystem.IsWindows()
            || !TryGetStationServiceIdentity(out var stationIdentity))
        {
            return;
        }

        var profileName = "OpenLineOps.Tests.LongPath." + Guid.NewGuid().ToString("N");
        var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(profileName);
        var contentCapabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
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
        var protectionPolicy = new ImmutableContentProtectionPolicy(
            contentCapabilitySid,
            stationIdentity.ServiceSid);
        await protector.ProtectAsync(
            contentDirectory,
            inventory,
            protectionPolicy);
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
            BestEffortDeleteTestTree(contentDirectory);
            if (Directory.Exists(cacheRoot)
                && !Directory.EnumerateFileSystemEntries(cacheRoot).Any())
            {
                Directory.Delete(cacheRoot);
            }
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
        var createdArtifacts = WindowsAppContainerIdentity.ProbeProfileArtifacts(
            profileName);
        Assert.True(createdArtifacts.PackageRootExists);
        Assert.True(createdArtifacts.ProfileDirectoryExists);
        Assert.True(createdArtifacts.MappingExists);
        Assert.True(createdArtifacts.MappingChildrenExists);
        Assert.True(createdArtifacts.StorageExists);
        Assert.True(createdArtifacts.StorageChildrenExists);

        Assert.True(WindowsAppContainerIdentity.DeleteProfile(profileName));

        Assert.False(Directory.Exists(profilePath));
        Assert.False(
            WindowsAppContainerIdentity.ProbeProfileArtifacts(profileName)
                .AnyArtifactsExist);
    }

    [Fact]
    public void ProfileArtifactProbeDetectsFilesystemOrphanWithoutMapping()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileName = "OpenLineOps.Tests.Orphan." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            profileName.ToLowerInvariant());
        Directory.CreateDirectory(Path.Combine(packageRoot, "AC"));
        try
        {
            var artifacts = WindowsAppContainerIdentity.ProbeProfileArtifacts(
                profileName);
            Assert.True(artifacts.PackageRootExists);
            Assert.True(artifacts.ProfileDirectoryExists);
            Assert.False(artifacts.MappingExists);
            Assert.False(artifacts.MappingChildrenExists);
            Assert.False(artifacts.StorageExists);
            Assert.False(artifacts.StorageChildrenExists);
            Assert.True(artifacts.AnyArtifactsExist);
            Assert.True(WindowsAppContainerIdentity.ProfileExists(profileName));
        }
        finally
        {
            _ = WindowsAppContainerIdentity.DeleteProfile(profileName);
            if (Directory.Exists(packageRoot))
            {
                Directory.Delete(packageRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ProfileBootstrapAddsManagerAccessBeforeRequestingWriteOwner()
    {
        const uint writeDac = 0x00040000;
        const uint writeOwner = 0x00080000;
        var masks = WindowsAppContainerProfileLifecycleAccess
            .ProfileAccessMasksForTesting();

        Assert.NotEqual(0u, masks.Bootstrap & writeDac);
        Assert.Equal(0u, masks.Bootstrap & writeOwner);
        Assert.Equal(0u, masks.Ownership & writeDac);
        Assert.NotEqual(0u, masks.Ownership & writeOwner);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void NewProfileConfigurationFailureRollsBackEveryCreatedArtifact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileName = "OpenLineOps.Tests.Rollback." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            profileName.ToLowerInvariant());
        var currentIdentity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "Current Windows identity has no user SID.");
        try
        {
            var exception = Assert.Throws<ProfileConfigurationFailureException>(() =>
            {
                using var capabilities = WindowsAppContainerSecurityCapabilities.Create(
                    new WindowsAppContainerPolicy(
                        profileName,
                        NetworkAccessAllowed: false),
                    checkpoint =>
                    {
                        if (checkpoint
                            != WindowsAppContainerProfileConfigurationCheckpoint.ProfileCreated)
                        {
                            return;
                        }

                        File.WriteAllText(
                            Path.Combine(packageRoot, "partial-configuration.probe"),
                            "must be rolled back");
                        WindowsAppContainerProfileLifecycleAccess
                            .GrantProfileTreeAccessForTesting(
                                packageRoot,
                                currentIdentity,
                                profileName);
                        WindowsAppContainerProfileLifecycleAccess
                            .GrantRegistryAccessForTesting(
                                WindowsAppContainerProfileLifecycleAccess
                                    .StorageKeyPath(profileName),
                                currentIdentity,
                                profileName,
                                "injected partial storage");
                        throw new ProfileConfigurationFailureException();
                    });
            });

            Assert.NotNull(exception);
            Assert.False(WindowsAppContainerIdentity.ProfileExists(profileName));
            Assert.False(Directory.Exists(packageRoot));
            using var storage = Registry.CurrentUser.OpenSubKey(
                WindowsAppContainerProfileLifecycleAccess.StorageKeyPath(profileName),
                writable: false);
            Assert.Null(storage);
        }
        finally
        {
            if (WindowsAppContainerIdentity.ProfileExists(profileName))
            {
                _ = WindowsAppContainerIdentity.DeleteProfile(profileName);
            }
        }
    }

    [Fact]
    public async Task SameProfileConfigurationIsSerializedWithinTheProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileName = "OpenLineOps.Tests.Serialized." + Guid.NewGuid().ToString("N");
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        try
        {
            var first = Task.Run(() =>
            {
                using var capabilities = WindowsAppContainerSecurityCapabilities.Create(
                    new WindowsAppContainerPolicy(
                        profileName,
                        NetworkAccessAllowed: false),
                    checkpoint =>
                    {
                        if (checkpoint
                            != WindowsAppContainerProfileConfigurationCheckpoint.ConfigurationEntered)
                        {
                            return;
                        }

                        firstEntered.Set();
                        Assert.True(releaseFirst.Wait(ProcessTimeout));
                    });
            });
            Assert.True(firstEntered.Wait(ProcessTimeout));

            var second = Task.Run(() =>
            {
                secondStarted.Set();
                using var capabilities = WindowsAppContainerSecurityCapabilities.Create(
                    new WindowsAppContainerPolicy(
                        profileName,
                        NetworkAccessAllowed: false),
                    checkpoint =>
                    {
                        if (checkpoint
                            == WindowsAppContainerProfileConfigurationCheckpoint.ConfigurationEntered)
                        {
                            secondEntered.Set();
                        }
                    });
            });
            Assert.True(secondStarted.Wait(ProcessTimeout));
            Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(250)));

            releaseFirst.Set();
            await Task.WhenAll(first, second).WaitAsync(ProcessTimeout);
            Assert.True(secondEntered.IsSet);
        }
        finally
        {
            releaseFirst.Set();
            if (WindowsAppContainerIdentity.ProfileExists(profileName))
            {
                _ = WindowsAppContainerIdentity.DeleteProfile(profileName);
            }
        }
    }

    [Fact]
    public async Task ProfileDeletionWaitsForActiveSecurityCapabilities()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileName = "OpenLineOps.Tests.DeleteLock."
                          + Guid.NewGuid().ToString("N");
        WindowsAppContainerSecurityCapabilities? capabilities = null;
        using var deleteStarted = new ManualResetEventSlim();
        using var deleteCompleted = new ManualResetEventSlim();
        try
        {
            capabilities = WindowsAppContainerSecurityCapabilities.Create(
                new WindowsAppContainerPolicy(
                    profileName,
                    NetworkAccessAllowed: false));
            var delete = Task.Run(() =>
            {
                deleteStarted.Set();
                _ = WindowsAppContainerIdentity.DeleteProfile(profileName);
                deleteCompleted.Set();
            });
            Assert.True(deleteStarted.Wait(ProcessTimeout));
            Assert.False(deleteCompleted.Wait(TimeSpan.FromMilliseconds(250)));

            capabilities.Dispose();
            capabilities = null;
            await delete.WaitAsync(ProcessTimeout);
            Assert.True(deleteCompleted.IsSet);
            Assert.False(WindowsAppContainerIdentity.ProfileExists(profileName));
        }
        finally
        {
            capabilities?.Dispose();
            if (WindowsAppContainerIdentity.ProfileExists(profileName))
            {
                _ = WindowsAppContainerIdentity.DeleteProfile(profileName);
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void StableProfileTreeGrantsAccessThroughTheValidatedHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = NewPath("profile-stable-handle-root");
        var nested = Path.Combine(root, "nested");
        var file = Path.Combine(nested, "profile-file.bin");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        try
        {
            var currentIdentity = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException(
                    "Current Windows identity has no user SID.");

            WindowsAppContainerProfileLifecycleAccess
                .GrantProfileTreeAccessForTesting(
                    root,
                    currentIdentity,
                    "OpenLineOps.Tests.StableHandles");

            foreach (var directory in new[] { root, nested })
            {
                var security = new DirectoryInfo(directory).GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access);
                Assert.Equal(
                    currentIdentity.Value,
                    Assert.IsType<SecurityIdentifier>(
                        security.GetOwner(typeof(SecurityIdentifier))).Value);
                var rule = Assert.Single(
                    security
                    .GetAccessRules(
                        includeExplicit: true,
                        includeInherited: false,
                        typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>(),
                    candidate =>
                        candidate.IdentityReference is SecurityIdentifier identity
                        && string.Equals(
                            identity.Value,
                            currentIdentity.Value,
                            StringComparison.Ordinal));
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(
                    FileSystemRights.FullControl,
                    rule.FileSystemRights & FileSystemRights.FullControl);
                Assert.Equal(
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    rule.InheritanceFlags);
            }

            var fileSecurity = new FileInfo(file).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            Assert.Equal(
                currentIdentity.Value,
                Assert.IsType<SecurityIdentifier>(
                    fileSecurity.GetOwner(typeof(SecurityIdentifier))).Value);
            var fileRule = Assert.Single(
                fileSecurity
                .GetAccessRules(
                    includeExplicit: true,
                    includeInherited: false,
                    typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(),
                candidate =>
                    candidate.IdentityReference is SecurityIdentifier identity
                    && string.Equals(
                        identity.Value,
                        currentIdentity.Value,
                        StringComparison.Ordinal));
            Assert.Equal(AccessControlType.Allow, fileRule.AccessControlType);
            Assert.Equal(
                FileSystemRights.FullControl,
                fileRule.FileSystemRights & FileSystemRights.FullControl);
            Assert.Equal(InheritanceFlags.None, fileRule.InheritanceFlags);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void StableProfileTreeRejectsFilesWithMoreThanOneHardLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = NewPath("profile-hard-link-root");
        var original = Path.Combine(root, "profile-file.bin");
        var outsideLink = NewPath("profile-hard-link-alias.bin");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(original, [1, 2, 3, 4]);
        try
        {
            Assert.True(CreateHardLink(outsideLink, original, IntPtr.Zero));
            var currentIdentity = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException(
                    "Current Windows identity has no user SID.");

            var exception = Assert.Throws<InvalidDataException>(() =>
                WindowsAppContainerProfileLifecycleAccess
                    .GrantProfileTreeAccessForTesting(
                        root,
                        currentIdentity,
                        "OpenLineOps.Tests.HardLink"));

            Assert.Contains(
                "exactly one hard link",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outsideLink);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProfileLifecycleManagerGetsIdempotentExactLeafAccess()
    {
        if (!TryGetStationServiceIdentity(out var stationIdentity))
        {
            return;
        }

        var managerServiceSid = stationIdentity.ServiceSid;
        var profileName = "OpenLineOps.Tests.Lifecycle." + Guid.NewGuid().ToString("N");
        try
        {
            var appContainerSid = WindowsAppContainerIdentity.EnsureProfile(
                profileName,
                managerServiceSid);
            var managerIdentity = new SecurityIdentifier(managerServiceSid);
            var profileDirectory = new DirectoryInfo(
                WindowsAppContainerIdentity.GetProfileFolderPath(appContainerSid));
            var damagedAccess = profileDirectory.GetAccessControl(
                AccessControlSections.Access);
            damagedAccess.PurgeAccessRules(managerIdentity);
            damagedAccess.AddAccessRule(new FileSystemAccessRule(
                managerIdentity,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            profileDirectory.SetAccessControl(damagedAccess);

            var registryLeafPaths = new[]
            {
                WindowsAppContainerProfileLifecycleAccess.MappingKeyPath(
                    appContainerSid),
                WindowsAppContainerProfileLifecycleAccess.MappingKeyPath(
                    appContainerSid) + "\\Children",
                WindowsAppContainerProfileLifecycleAccess.StorageKeyPath(
                    profileName),
                WindowsAppContainerProfileLifecycleAccess.StorageKeyPath(
                    profileName) + "\\Children"
            };
            foreach (var keyPath in registryLeafPaths)
            {
                using var damagedKey = Registry.CurrentUser.OpenSubKey(
                    keyPath,
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.FullControl);
                Assert.NotNull(damagedKey);
                var damagedRegistryAccess = damagedKey!.GetAccessControl(
                    AccessControlSections.Access);
                damagedRegistryAccess.PurgeAccessRules(managerIdentity);
                damagedRegistryAccess.AddAccessRule(new RegistryAccessRule(
                    managerIdentity,
                    RegistryRights.QueryValues,
                    InheritanceFlags.ContainerInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                damagedKey.SetAccessControl(damagedRegistryAccess);
                damagedKey.Flush();
            }

            Assert.Equal(
                appContainerSid,
                WindowsAppContainerIdentity.EnsureProfile(
                    profileName,
                    managerServiceSid));

            var directoryRules = profileDirectory.GetAccessControl(
                    AccessControlSections.Access)
                .GetAccessRules(
                    includeExplicit: true,
                    includeInherited: false,
                    typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(rule =>
                    rule.IdentityReference is SecurityIdentifier identity
                    && string.Equals(
                        identity.Value,
                        managerIdentity.Value,
                        StringComparison.Ordinal)
                    && rule.AccessControlType == AccessControlType.Allow)
                .ToArray();
            var directoryRule = Assert.Single(directoryRules);
            Assert.Equal(
                managerServiceSid,
                Assert.IsType<SecurityIdentifier>(
                    profileDirectory.GetAccessControl(AccessControlSections.Owner)
                        .GetOwner(typeof(SecurityIdentifier))).Value);
            Assert.Equal(
                FileSystemRights.FullControl,
                directoryRule.FileSystemRights & FileSystemRights.FullControl);
            Assert.Equal(
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                directoryRule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, directoryRule.PropagationFlags);

            foreach (var keyPath in registryLeafPaths)
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
                Assert.NotNull(key);
                var security = key!.GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access);
                Assert.Equal(
                    managerServiceSid,
                    Assert.IsType<SecurityIdentifier>(
                        security.GetOwner(typeof(SecurityIdentifier))).Value);
                var registryRules = security
                    .GetAccessRules(
                        includeExplicit: true,
                        includeInherited: false,
                        typeof(SecurityIdentifier))
                    .Cast<RegistryAccessRule>()
                    .Where(rule =>
                        rule.IdentityReference is SecurityIdentifier identity
                        && string.Equals(
                            identity.Value,
                            managerIdentity.Value,
                            StringComparison.Ordinal)
                        && rule.AccessControlType == AccessControlType.Allow)
                    .ToArray();
                var registryRule = Assert.Single(registryRules);
                Assert.Equal(
                    RegistryRights.FullControl,
                    registryRule.RegistryRights & RegistryRights.FullControl);
                Assert.Equal(
                    InheritanceFlags.ContainerInherit,
                    registryRule.InheritanceFlags);
                Assert.Equal(PropagationFlags.None, registryRule.PropagationFlags);
            }
        }
        finally
        {
            _ = WindowsAppContainerIdentity.DeleteProfile(profileName);
        }

        Assert.False(WindowsAppContainerIdentity.ProfileExists(profileName));
    }

    [Fact]
    public void ProfileLifecycleManagerRejectsBroadIdentityBeforeProfileCreation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profileName = "OpenLineOps.Tests.Reject."
                          + Guid.NewGuid().ToString("N");
        var broadIdentity = new SecurityIdentifier(
            WellKnownSidType.WorldSid,
            domainSid: null).Value;

        var exception = Assert.Throws<ArgumentException>(() =>
            WindowsAppContainerIdentity.EnsureProfile(profileName, broadIdentity));

        Assert.Contains(
            "exact canonical Windows service SID",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(WindowsAppContainerIdentity.ProfileExists(profileName));
    }

    [Fact]
    public void ProfileLifecycleManagerRejectsNonCurrentServiceBeforeProfileCreation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string otherServiceSid = "S-1-5-80-1-2-3-4-5";
        var profileName = "OpenLineOps.Tests.Mismatch."
                          + Guid.NewGuid().ToString("N");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WindowsAppContainerIdentity.EnsureProfile(
                profileName,
                otherServiceSid));

        Assert.Contains(
            "Station",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(WindowsAppContainerIdentity.ProfileExists(profileName));
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
    public async Task ClosingJobKillsImmediateExitChildrenAndClosesEveryOwnedHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int repetitions = 50;
        for (var iteration = 0; iteration < repetitions; iteration++)
        {
            var pidFile = NewPath($"child-{iteration.ToString(CultureInfo.InvariantCulture)}.pid");
            int childProcessId;
            var launched = Launch(
                ["sandbox-spawn-child-and-exit", pidFile, "60000"],
                EnvironmentForChild());
            try
            {
                launched.StandardInput.Dispose();
                using var timeout = new CancellationTokenSource(ProcessTimeout);
                await launched.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, launched.ExitCode);
                childProcessId = await ReadProcessIdAsync(pidFile, timeout.Token);
                Assert.True(IsProcessRunning(childProcessId));
                AssertExpectedHandleStateBeforeDisposal(launched.OwnedHandleState);
            }
            finally
            {
                launched.Dispose();
                AssertEveryOwnedHandleClosed(launched.OwnedHandleState);
            }

            await AssertProcessExitedAsync(childProcessId);
            File.Delete(pidFile);
        }
    }

    [Fact]
    public void DisposeAttemptsEveryResourceBeforeReportingAggregateFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var standardInput = new ThrowingDisposeStream(
            new IOException("synthetic standard input release failure"));
        var standardOutput = new ThrowingDisposeStream(
            new NotSupportedException("synthetic standard output release failure"));
        var standardError = new ThrowingDisposeStream(
            new InvalidOperationException("synthetic standard error release failure"));
        var process = Process.GetCurrentProcess();
        var processHandle = new SafeProcessHandle(process.Handle, ownsHandle: false);
        var job = WindowsProcessJob.CreateKillOnClose();
        var isolated = new WindowsIsolatedProcess(
            process,
            processHandle,
            standardInput,
            standardOutput,
            standardError,
            job,
            process.SafeHandle);

        var failure = Assert.Throws<AggregateException>(isolated.Dispose);

        Assert.Equal(3, failure.Flatten().InnerExceptions.Count);
        Assert.Equal(1, standardInput.DisposeCalls);
        Assert.Equal(1, standardOutput.DisposeCalls);
        Assert.Equal(1, standardError.DisposeCalls);
        Assert.True(job.IsClosed);
        Assert.True(processHandle.IsClosed);
        isolated.Dispose();
    }

    [Fact]
    public async Task ConcurrentDisposeWaitsUntilTheFirstReleaseCompletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var disposeEntered = new ManualResetEventSlim();
        using var allowDispose = new ManualResetEventSlim();
        var standardInput = new BlockingDisposeStream(disposeEntered, allowDispose);
        var process = Process.GetCurrentProcess();
        var processHandle = new SafeProcessHandle(process.Handle, ownsHandle: false);
        var job = WindowsProcessJob.CreateKillOnClose();
        var isolated = new WindowsIsolatedProcess(
            process,
            processHandle,
            standardInput,
            new MemoryStream(),
            new MemoryStream(),
            job,
            process.SafeHandle);
        var firstDispose = Task.Run(isolated.Dispose);
        Assert.True(disposeEntered.Wait(ProcessTimeout));
        var secondStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDispose = Task.Run(() =>
        {
            secondStarted.SetResult(true);
            isolated.Dispose();
        });

        try
        {
            await secondStarted.Task.WaitAsync(ProcessTimeout);
            await Task.Delay(100);
            Assert.False(secondDispose.IsCompleted);
        }
        finally
        {
            allowDispose.Set();
            await Task.WhenAll(firstDispose, secondDispose).WaitAsync(ProcessTimeout);
        }

        Assert.Equal(1, standardInput.DisposeCalls);
        Assert.True(job.IsClosed);
        Assert.True(processHandle.IsClosed);
    }

    [Fact]
    public async Task ProcessTreeWaitOutlivesNormallyExitedRootUntilChildExits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pidFile = NewPath("normal-root-exit-child.pid");
        int childProcessId;
        using (var launched = Launch(
                   ["sandbox-spawn-child-and-exit", pidFile, "2000"],
                   EnvironmentForChild()))
        {
            launched.StandardInput.Dispose();
            using var timeout = new CancellationTokenSource(ProcessTimeout);
            await launched.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, launched.ExitCode);
            childProcessId = await ReadProcessIdAsync(pidFile, timeout.Token);
            Assert.True(IsProcessRunning(childProcessId));

            var processTreeExit = launched.WaitForProcessTreeExitAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);
            Assert.False(processTreeExit.IsCompleted);
            await processTreeExit;
            Assert.Equal(0u, launched.ActiveProcessCount);
        }

        await AssertProcessExitedAsync(childProcessId);
        File.Delete(pidFile);
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
            using var treeTimeout = new CancellationTokenSource(ProcessTimeout);
            await launched.WaitForProcessTreeExitAsync(treeTimeout.Token);
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

    private static void AssertEveryOwnedHandleClosed(
        WindowsIsolatedProcessHandleState state)
    {
        Assert.True(state.JobHandleClosed);
        Assert.True(state.CreateProcessHandleClosed);
        Assert.True(state.ManagedProcessHandleClosed);
        Assert.True(state.StandardInputPipeHandleClosed);
        Assert.True(state.StandardOutputPipeHandleClosed);
        Assert.True(state.StandardErrorPipeHandleClosed);
        Assert.True(state.PrimaryThreadHandleClosed);
        Assert.True(state.ChildStandardInputPipeHandleClosed);
        Assert.True(state.ChildStandardOutputPipeHandleClosed);
        Assert.True(state.ChildStandardErrorPipeHandleClosed);
    }

    private static void AssertExpectedHandleStateBeforeDisposal(
        WindowsIsolatedProcessHandleState state)
    {
        Assert.False(state.JobHandleClosed);
        Assert.False(state.CreateProcessHandleClosed);
        Assert.False(state.ManagedProcessHandleClosed);
        Assert.True(state.StandardInputPipeHandleClosed);
        Assert.False(state.StandardOutputPipeHandleClosed);
        Assert.False(state.StandardErrorPipeHandleClosed);
        Assert.True(state.PrimaryThreadHandleClosed);
        Assert.True(state.ChildStandardInputPipeHandleClosed);
        Assert.True(state.ChildStandardOutputPipeHandleClosed);
        Assert.True(state.ChildStandardErrorPipeHandleClosed);
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

    private static bool TryGetStationServiceIdentity(
        out WindowsStationServiceIdentity identity)
    {
        identity = null!;
        var configuredServiceName = Environment.GetEnvironmentVariable(
            "OPENLINEOPS_TEST_WINDOWS_SERVICE_NAME");
        if (!OperatingSystem.IsWindows()
            || !WindowsStationServiceIdentityReader.IsCanonicalServiceName(
                configuredServiceName))
        {
            return false;
        }

        try
        {
            identity = WindowsStationServiceIdentityReader.ReadRequired(
                WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                    configuredServiceName!));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void BestEffortDeleteTestTree(string root)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
            }

            File.SetAttributes(root, File.GetAttributes(root) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            // Restricted-token SCM tests leave sealed content for their elevated harness.
        }
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

    private sealed class ProfileConfigurationFailureException : Exception;

    private sealed class ThrowingDisposeStream(Exception failure) : MemoryStream
    {
        public int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCalls++;
            try
            {
                throw failure;
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }

    private sealed class BlockingDisposeStream(
        ManualResetEventSlim disposeEntered,
        ManualResetEventSlim allowDispose) : MemoryStream
    {
        public int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCalls++;
            try
            {
                disposeEntered.Set();
                Assert.True(allowDispose.Wait(ProcessTimeout));
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }

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

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
