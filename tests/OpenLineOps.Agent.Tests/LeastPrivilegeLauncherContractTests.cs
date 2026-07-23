using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.Runtime.Infrastructure.Scripting;

namespace OpenLineOps.Agent.Tests;

[Collection(LeastPrivilegeLauncherTestGroup.Name)]
public sealed class LeastPrivilegeLauncherContractTests
{
    private const string Identity = "PerExecutionAppContainer";
    private const string RestrictedHostMessage =
        "The Least Privilege Launcher cannot create an AppContainer from an already restricted token.";
    private const string PythonRuntimeAuthorizationMessage =
        "The Python runtime is not provisioned for OpenLineOps AppContainer execution.";
    private const string ProfilePrefix = "OpenLineOps.ScriptWorker.";
    private const string MarkerExtension = ".active.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TheoryData<string[], string> RejectedCommands => new()
    {
        {
            [],
            "Expected exactly the non-interactive PerExecutionAppContainer worker launch protocol."
        },
        {
            ["-n", "-u", "another-identity", "--", "env"],
            "Expected exactly the non-interactive PerExecutionAppContainer worker launch protocol."
        },
        {
            [
                "-n",
                "-u",
                Identity,
                "--",
                "env",
                "UNREVIEWED=value",
                RequiredIsolationAssignment(),
                WorkerPath()
            ],
            "Unsupported least-privilege worker environment assignment 'UNREVIEWED'."
        },
        {
            [
                "-n",
                "-u",
                Identity,
                "--",
                "env",
                RequiredIsolationAssignment(),
                RequiredLeastPrivilegeAssignment(),
                RequiredIdentityAssignment(),
                Path.Combine(Environment.SystemDirectory, "whoami.exe")
            ],
            "The launcher can execute only the co-packaged OpenLineOps.ScriptWorker.exe."
        },
        {
            [
                "-n",
                "-u",
                Identity,
                "--",
                "env",
                RequiredIsolationAssignment(),
                RequiredLeastPrivilegeAssignment(),
                RequiredIdentityAssignment(),
                WorkerPath(),
                "unexpected-worker-argument"
            ],
            "The bundled Python Script Worker does not accept launcher arguments."
        },
        {
            [
                "provision-python-runtime",
                "--runtime-dll",
                WorkerPath(),
                "unexpected-provisioning-argument"
            ],
            "Expected exactly 'provision-python-runtime --runtime-dll <absolute-path>'."
        }
    };

    [Theory]
    [MemberData(nameof(RejectedCommands))]
    public async Task LauncherRejectsEveryProtocolExtension(
        string[] arguments,
        string expectedError)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executablePath = Path.Combine(
            LauncherBundleRoot(),
            "OpenLineOps.LeastPrivilegeLauncher.exe");
        Assert.True(File.Exists(executablePath), $"Missing test launcher: {executablePath}");

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Least Privilege Launcher did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(78, process.ExitCode);
        Assert.Equal(string.Empty, await standardOutput);
        Assert.Contains(expectedError, await standardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisioningCommandGrantsRuntimeCapabilityRecursively()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps.Tests",
            "Provisioning",
            Guid.NewGuid().ToString("N"));
        var nestedRoot = Path.Combine(runtimeRoot, "Lib", "nested");
        var runtimeDll = Path.Combine(runtimeRoot, "python-test.dll");
        var standardLibraryFile = Path.Combine(runtimeRoot, "Lib", "os.py");
        var nestedFile = Path.Combine(nestedRoot, "module.py");
        Directory.CreateDirectory(nestedRoot);
        await File.WriteAllTextAsync(runtimeDll, "runtime");
        await File.WriteAllTextAsync(standardLibraryFile, "standard-library");
        await File.WriteAllTextAsync(nestedFile, "module");
        try
        {
            var result = await RunLauncherCommandAsync(
                "provision-python-runtime",
                "--runtime-dll",
                runtimeDll);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardError);
            using var evidence = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(
                "PythonRuntimeProvisioned",
                evidence.RootElement.GetProperty("Operation").GetString());
            Assert.Equal(
                runtimeRoot,
                evidence.RootElement.GetProperty("RuntimeRoot").GetString(),
                ignoreCase: true);
            Assert.Equal(
                runtimeDll,
                evidence.RootElement.GetProperty("RuntimeDll").GetString(),
                ignoreCase: true);
            var capabilitySid = new SecurityIdentifier(
                evidence.RootElement.GetProperty("CapabilitySid").GetString()
                ?? throw new InvalidDataException(
                    "Provisioning evidence omitted the capability SID."));
            AssertReadExecuteRule(runtimeRoot, capabilitySid, requireInheritance: true);
            AssertReadExecuteRule(nestedRoot, capabilitySid, requireInheritance: true);
            AssertReadExecuteRule(runtimeDll, capabilitySid, requireInheritance: false);
            AssertReadExecuteRule(
                standardLibraryFile,
                capabilitySid,
                requireInheritance: false);
            AssertReadExecuteRule(nestedFile, capabilitySid, requireInheritance: false);
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
            {
                Directory.Delete(runtimeRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProvisioningCommandRejectsReparseDescendantWithoutChangingTargetAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps.Tests",
            "ProvisioningReparse",
            Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(testRoot, "runtime");
        var targetRoot = Path.Combine(testRoot, "outside");
        var runtimeDll = Path.Combine(runtimeRoot, "python-test.dll");
        var standardLibraryFile = Path.Combine(runtimeRoot, "Lib", "os.py");
        var targetFile = Path.Combine(targetRoot, "must-not-be-authorized.txt");
        var junction = Path.Combine(runtimeRoot, "redirected");
        Directory.CreateDirectory(Path.GetDirectoryName(standardLibraryFile)!);
        Directory.CreateDirectory(targetRoot);
        await File.WriteAllTextAsync(runtimeDll, "runtime");
        await File.WriteAllTextAsync(standardLibraryFile, "standard-library");
        await File.WriteAllTextAsync(targetFile, "outside");
        CreateDirectoryJunction(junction, targetRoot);
        var targetAclBefore = FileSystemAclExtensions
            .GetAccessControl(new FileInfo(targetFile))
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        try
        {
            var result = await RunLauncherCommandAsync(
                "provision-python-runtime",
                "--runtime-dll",
                runtimeDll);

            Assert.Equal(78, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains(
                "Content authorization cannot traverse reparse points.",
                result.StandardError,
                StringComparison.Ordinal);
            var targetAclAfter = FileSystemAclExtensions
                .GetAccessControl(new FileInfo(targetFile))
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.Equal(targetAclBefore, targetAclAfter);
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProvisioningCommandRejectsBlockingCapabilityDeny()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps.Tests",
            "ProvisioningDeny",
            Guid.NewGuid().ToString("N"));
        var runtimeDll = Path.Combine(runtimeRoot, "python-test.dll");
        var standardLibraryFile = Path.Combine(runtimeRoot, "Lib", "os.py");
        Directory.CreateDirectory(Path.GetDirectoryName(standardLibraryFile)!);
        await File.WriteAllTextAsync(runtimeDll, "runtime");
        await File.WriteAllTextAsync(standardLibraryFile, "standard-library");
        var capability = new SecurityIdentifier(
            WindowsAppContainerIdentity.EnsureCapabilitySid(
                WindowsAppContainerIdentity.PythonRuntimeCapabilityName));
        var security = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(runtimeRoot));
        security.AddAccessRule(new FileSystemAccessRule(
            capability,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Deny));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(runtimeRoot), security);
        try
        {
            var result = await RunLauncherCommandAsync(
                "provision-python-runtime",
                "--runtime-dll",
                runtimeDll);

            Assert.Equal(78, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains(
                PythonRuntimeAuthorizationMessage,
                result.StandardError,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
            {
                Directory.Delete(runtimeRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LauncherRejectsWriteCapablePythonRuntimeDescendantAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps.Tests",
            "WriteCapablePython",
            Guid.NewGuid().ToString("N"));
        var runtimeDll = Path.Combine(runtimeRoot, "python-test.dll");
        var standardLibraryFile = Path.Combine(runtimeRoot, "Lib", "os.py");
        var driftedModule = Path.Combine(runtimeRoot, "Lib", "drifted.py");
        Directory.CreateDirectory(Path.GetDirectoryName(standardLibraryFile)!);
        await File.WriteAllTextAsync(runtimeDll, "runtime");
        await File.WriteAllTextAsync(standardLibraryFile, "standard-library");
        await File.WriteAllTextAsync(driftedModule, "module");
        try
        {
            var provisioned = await RunLauncherCommandAsync(
                "provision-python-runtime",
                "--runtime-dll",
                runtimeDll);
            Assert.Equal(0, provisioned.ExitCode);
            Assert.Equal(string.Empty, provisioned.StandardError);
            using (var evidence = JsonDocument.Parse(provisioned.StandardOutput))
            {
                Assert.Equal(
                    runtimeRoot,
                    evidence.RootElement.GetProperty("RuntimeRoot").GetString(),
                    ignoreCase: true);
                Assert.Equal(
                    runtimeDll,
                    evidence.RootElement.GetProperty("RuntimeDll").GetString(),
                    ignoreCase: true);
            }

            var capability = new SecurityIdentifier(
                WindowsAppContainerIdentity.EnsureCapabilitySid(
                    WindowsAppContainerIdentity.PythonRuntimeCapabilityName));
            var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(driftedModule));
            security.AddAccessRule(new FileSystemAccessRule(
                capability,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(driftedModule), security);

            using var process = StartLauncher(runtimeDll);
            process.StandardInput.Close();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);

            var result = new LauncherResult(
                process.ExitCode,
                await standardOutput,
                await standardError);
            Assert.Equal(78, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains(
                PythonRuntimeAuthorizationMessage,
                result.StandardError,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
            {
                Directory.Delete(runtimeRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LauncherFailsClosedWhenPythonRuntimeWasNotProvisioned()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps.Tests",
            "UnprovisionedPython",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeRoot);
        var runtimeDll = Path.Combine(runtimeRoot, "python-test.dll");
        await File.WriteAllTextAsync(runtimeDll, "runtime");
        try
        {
            using var process = StartLauncher(runtimeDll);
            process.StandardInput.Close();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                throw;
            }

            Assert.Equal(78, process.ExitCode);
            Assert.Equal(string.Empty, await standardOutput);
            Assert.Contains(
                PythonRuntimeAuthorizationMessage,
                await standardError,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
            {
                Directory.Delete(runtimeRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LauncherBootsBundledWorkerThroughPerExecutionAppContainer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunWithEmptyInputAsync();
        if (IsExpectedManagedHostRejection(result))
        {
            return;
        }

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains(
            "Python script worker request body is required.",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(ExistingMarkers(RuntimeParentPath()));
    }

    [Fact]
    public async Task LauncherRejectsRuntimeAssetDirectoryJunctionBeforeReadingExternalFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimeIdentifier = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException(
                "The test requires a supported Windows architecture.")
        };
        var bundleRoot = LauncherBundleRoot();
        var nativeRoot = Path.Combine(
            bundleRoot,
            "runtimes",
            runtimeIdentifier,
            "native");
        var nativeAssets = Directory.EnumerateFiles(nativeRoot).ToArray();
        Assert.NotEmpty(nativeAssets);
        var backupRoot = nativeRoot + ".backup-" + Guid.NewGuid().ToString("N");
        var externalRoot = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps.Tests",
            "LauncherPayloadJunction",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        foreach (var nativeAsset in nativeAssets)
        {
            File.Copy(
                nativeAsset,
                Path.Combine(externalRoot, Path.GetFileName(nativeAsset)));
        }
        var externalAsset = Path.Combine(externalRoot, Path.GetFileName(nativeAssets[0]));
        var expectedExternalBytes = await File.ReadAllBytesAsync(externalAsset);
        Directory.Move(nativeRoot, backupRoot);
        try
        {
            CreateDirectoryJunction(nativeRoot, externalRoot);
            LauncherResult result;
            await using (var lockedExternalAsset = new FileStream(
                             externalAsset,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.None,
                             bufferSize: 1,
                             FileOptions.Asynchronous))
            {
                result = await RunWithEmptyInputAsync();
            }

            Assert.Equal(78, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains(
                "Python Script Worker payload directory cannot be a reparse point",
                result.StandardError,
                StringComparison.Ordinal);
            Assert.Equal(expectedExternalBytes, await File.ReadAllBytesAsync(externalAsset));
        }
        finally
        {
            if (Directory.Exists(nativeRoot))
            {
                Directory.Delete(nativeRoot, recursive: false);
            }
            if (Directory.Exists(backupRoot))
            {
                Directory.Move(backupRoot, nativeRoot);
            }
            if (Directory.Exists(externalRoot))
            {
                Directory.Delete(externalRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LauncherRejectsNonObjectRuntimeTargetMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dependenciesPath = Path.Combine(
            LauncherBundleRoot(),
            "OpenLineOps.ScriptWorker.deps.json");
        var original = await File.ReadAllBytesAsync(dependenciesPath);
        var root = JsonNode.Parse(original)?.AsObject()
                   ?? throw new InvalidDataException(
                       "The test Script Worker dependency manifest is invalid.");
        var runtimeTargetName = root["runtimeTarget"]?["name"]?.GetValue<string>()
                                ?? throw new InvalidDataException(
                                    "The test Script Worker runtime target is missing.");
        var target = root["targets"]?[runtimeTargetName]?.AsObject()
                     ?? throw new InvalidDataException(
                         "The test Script Worker target is missing.");
        var runtimeTargets = target
            .Select(library => library.Value?["runtimeTargets"])
            .OfType<JsonObject>()
            .First();
        var assetName = runtimeTargets.First().Key;
        runtimeTargets[assetName] = "invalid-runtime-target";

        LauncherResult result;
        try
        {
            await File.WriteAllTextAsync(
                dependenciesPath,
                root.ToJsonString(JsonOptions));
            result = await RunWithEmptyInputAsync();
        }
        finally
        {
            await File.WriteAllBytesAsync(dependenciesPath, original);
        }

        Assert.Equal(78, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains(
            "dependency manifest contains an invalid runtime target asset",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentWorkersUseDistinctAppContainersAndKillDescendants()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var probe = await RunWithEmptyInputAsync();
        if (IsExpectedManagedHostRejection(probe))
        {
            return;
        }
        Assert.Equal(2, probe.ExitCode);

        var trustedParent = RuntimeParentPath();
        SimulateInterruptedMarkerParentInitialization(trustedParent);
        RunningLauncher? first = null;
        RunningLauncher? second = null;
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var hostilePath = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps.Tests",
            "HostilePath",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostilePath);
        var launcherBundleRoot = LauncherBundleRoot();
        var unrelatedAssemblyPath = Path.Combine(
            launcherBundleRoot,
            "OpenLineOps.Agent.Tests.dll");
        var unrelatedDataRoot = Path.Combine(launcherBundleRoot, "data");
        Assert.False(File.Exists(unrelatedAssemblyPath));
        Assert.False(Directory.Exists(unrelatedDataRoot));
        await File.WriteAllTextAsync(unrelatedAssemblyPath, "not-a-worker-dependency");
        Directory.CreateDirectory(unrelatedDataRoot);
        await File.WriteAllTextAsync(
            Path.Combine(unrelatedDataRoot, "sentinel.txt"),
            "must-not-be-copied");
        Environment.SetEnvironmentVariable("PATH", hostilePath);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            first = await StartAndCaptureProfileAsync(
                ExistingMarkers(trustedParent),
                trustedParent);
            var firstContentRoot = Path.GetFullPath(Path.Combine(
                first.RuntimeRoot,
                "..",
                "content"));
            AssertCopiedWorkerPayload(firstContentRoot, launcherBundleRoot);
            Assert.False(File.Exists(Path.Combine(
                firstContentRoot,
                "OpenLineOps.Agent.Tests.dll")));
            Assert.False(Directory.Exists(Path.Combine(firstContentRoot, "data")));
            var anchorPath = Path.Combine(first.RuntimeRoot, "host-anchor.txt");
            await File.WriteAllTextAsync(anchorPath, "host-owned");

            second = await StartAndCaptureProfileAsync(
                ExistingMarkers(trustedParent),
                trustedParent);
            Assert.NotEqual(first.ProfileName, second.ProfileName);
            Assert.NotEqual(first.AppContainerSid, second.AppContainerSid);
            var request = CreateIsolationAttackRequest(
                first.RuntimeRoot,
                ((IPEndPoint)listener.LocalEndpoint).Port);
            await second.Process.StandardInput.WriteAsync(
                JsonSerializer.Serialize(request, JsonOptions));
            await second.Process.StandardInput.FlushAsync();
            second.Process.StandardInput.Close();

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            {
                await second.Process.WaitForExitAsync(timeout.Token);
            }
            var secondOutput = await second.StandardOutput;
            var secondError = await second.StandardError;
            Assert.Equal(0, second.Process.ExitCode);

            var workerResult = JsonSerializer.Deserialize<PythonScriptWorkerExecutionResult>(
                secondOutput,
                JsonOptions);
            Assert.NotNull(workerResult);
            Assert.True(
                string.Equals(workerResult.Outcome, "Completed", StringComparison.Ordinal),
                "The AppContainer isolation probe failed: "
                + workerResult.Reason
                + Environment.NewLine
                + secondError
                + Environment.NewLine
                + workerResult.Payload);
            Assert.Null(workerResult.Reason);
            Assert.False(string.IsNullOrWhiteSpace(workerResult.Payload));
            using var payload = JsonDocument.Parse(workerResult.Payload);
            Assert.True(payload.RootElement.GetProperty("token_is_app_container").GetBoolean());
            Assert.Equal(
                second.AppContainerSid,
                payload.RootElement.GetProperty("app_container_sid").GetString());
            Assert.Equal(4096, payload.RootElement.GetProperty("integrity_rid").GetInt32());
            Assert.False(payload.RootElement.GetProperty("network_connect_allowed").GetBoolean());
            Assert.True(
                payload.RootElement
                    .GetProperty("python_runtime_capability_present")
                    .GetBoolean());
            Assert.False(
                payload.RootElement
                    .GetProperty("internet_client_capability_present")
                    .GetBoolean());
            Assert.Equal(
                second.RuntimeRoot,
                payload.RootElement.GetProperty("runtime_root").GetString(),
                ignoreCase: true);
            Assert.True(payload.RootElement.GetProperty("own_write").GetBoolean());
            var workerPath = payload.RootElement.GetProperty("worker_path").GetString();
            Assert.NotNull(workerPath);
            Assert.DoesNotContain(hostilePath, workerPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                string.Join(
                    Path.PathSeparator,
                    Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32"),
                    Path.GetDirectoryName(AppContainerPythonRuntimeTestSupport.ResolveRuntimeDll())),
                workerPath,
                ignoreCase: true);
            Assert.True(payload.RootElement.GetProperty("readonly_file_created").GetBoolean());
            Assert.Equal(48, payload.RootElement.GetProperty("deep_tree_depth").GetInt32());
            Assert.Equal(512, payload.RootElement.GetProperty("wide_file_count").GetInt32());
            foreach (var operation in payload.RootElement.GetProperty("operations").EnumerateObject())
            {
                Assert.False(
                    operation.Value.GetProperty("allowed").GetBoolean(),
                    $"Cross-profile operation '{operation.Name}' unexpectedly succeeded.");
                var nativeError = operation.Value.GetProperty("native_error").GetInt32();
                Assert.True(
                    nativeError is 5 or 13,
                    $"Cross-profile operation '{operation.Name}' failed unexpectedly: {nativeError}.");
            }
            Assert.Equal(5, payload.RootElement.GetProperty("acl_change_result").GetInt32());

            var childProcessId = payload.RootElement.GetProperty("child_pid").GetInt32();
            Assert.True(payload.RootElement.GetProperty("child_was_running").GetBoolean());
            await AssertProcessExitedAsync(childProcessId);
            Assert.False(Directory.Exists(second.RuntimeRoot));
            Assert.False(File.Exists(second.MarkerPath));
            Assert.False(WindowsAppContainerIdentity.ProfileExists(second.ProfileName));
            Assert.True(File.Exists(anchorPath));
            Assert.Equal("host-owned", await File.ReadAllTextAsync(anchorPath));
            Assert.DoesNotContain(
                "Could not remove AppContainer profile",
                secondError,
                StringComparison.Ordinal);

            first.Process.StandardInput.Close();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await first.Process.WaitForExitAsync(timeout.Token);
            }
            Assert.Equal(2, first.Process.ExitCode);
            Assert.Equal(string.Empty, await first.StandardOutput);
            Assert.Contains(
                "Python script worker request body is required.",
                await first.StandardError,
                StringComparison.Ordinal);
            Assert.False(Directory.Exists(first.RuntimeRoot));
            Assert.False(File.Exists(first.MarkerPath));
            Assert.False(WindowsAppContainerIdentity.ProfileExists(first.ProfileName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            listener.Stop();
            if (second is not null)
            {
                await StopLauncherAsync(second);
            }
            if (first is not null)
            {
                await StopLauncherAsync(first);
            }
            File.Delete(unrelatedAssemblyPath);
            if (Directory.Exists(unrelatedDataRoot))
            {
                Directory.Delete(unrelatedDataRoot, recursive: true);
            }
            Directory.Delete(hostilePath, recursive: true);
        }
    }

    [Fact]
    public async Task StaleAppContainerProfileIsRecoveredBeforeNextLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var probe = await RunWithEmptyInputAsync();
        if (IsExpectedManagedHostRejection(probe))
        {
            return;
        }
        Assert.Equal(2, probe.ExitCode);

        var markerParent = RuntimeParentPath();
        Directory.CreateDirectory(markerParent);
        var staleProfileName = ProfilePrefix + Guid.NewGuid().ToString("N");
        var staleSid = WindowsAppContainerIdentity.EnsureProfile(staleProfileName);
        var staleRoot = WindowsAppContainerIdentity.GetProfileFolderPath(staleSid);
        var staleMarker = Path.Combine(markerParent, staleProfileName + MarkerExtension);
        await File.WriteAllTextAsync(
            staleMarker,
            JsonSerializer.Serialize(
                new ActiveProfileMarker(staleProfileName, staleSid, staleRoot),
                JsonOptions));
        try
        {
            var result = await RunWithEmptyInputAsync();
            Assert.Equal(2, result.ExitCode);
            Assert.False(File.Exists(staleMarker));
            Assert.False(Directory.Exists(staleRoot));
            Assert.False(WindowsAppContainerIdentity.ProfileExists(staleProfileName));
        }
        finally
        {
            File.Delete(staleMarker);
            if (WindowsAppContainerIdentity.ProfileExists(staleProfileName))
            {
                WindowsAppContainerIdentity.DeleteProfile(staleProfileName);
            }
        }
    }

    private static async Task<LauncherResult> RunWithEmptyInputAsync()
    {
        using var process = StartLauncher();
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }

        return new LauncherResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static bool IsExpectedManagedHostRejection(LauncherResult result)
    {
        if (result.ExitCode != 78)
        {
            return false;
        }

        var stagedBundleRoot = Environment.GetEnvironmentVariable(
            "OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT");
        var requireRealBoundary = string.Equals(
            Environment.GetEnvironmentVariable("OPENLINEOPS_REQUIRE_REAL_APPCONTAINER"),
            "1",
            StringComparison.Ordinal);
        var isGitHubActions = string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);
        var isCodexManagedHost =
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("CODEX_PERMISSION_PROFILE"))
            || (string.Equals(
                    Environment.GetEnvironmentVariable("CODEX_CI"),
                    "1",
                    StringComparison.Ordinal)
                && string.Equals(
                    Environment.GetEnvironmentVariable("CODEX_SHELL"),
                    "1",
                    StringComparison.Ordinal));
        Assert.True(
            !isGitHubActions
            && !requireRealBoundary
            && string.IsNullOrWhiteSpace(stagedBundleRoot)
            && isCodexManagedHost,
            "A release gate or clean Windows host must execute the real AppContainer boundary: "
            + result.StandardError);
        Assert.True(
            result.StandardError.Contains(RestrictedHostMessage, StringComparison.Ordinal)
            || result.StandardError.Contains(
                PythonRuntimeAuthorizationMessage,
                StringComparison.Ordinal),
            "The managed host rejected the launcher for an unexpected reason: "
            + result.StandardError);
        return true;
    }

    private static Process StartLauncher()
    {
        var runtimeDll = AppContainerPythonRuntimeTestSupport.ResolveRuntimeDll();
        return StartLauncher(runtimeDll);
    }

    private static Process StartLauncher(string runtimeDll)
    {
        var workerPath = WorkerPath();
        Assert.True(File.Exists(workerPath), $"Missing test worker: {workerPath}");
        var launcherPath = Path.Combine(
            LauncherBundleRoot(),
            "OpenLineOps.LeastPrivilegeLauncher.exe");
        Assert.True(File.Exists(launcherPath), $"Missing test launcher: {launcherPath}");
        var startInfo = new ProcessStartInfo(launcherPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.Environment["PYTHONNET_PYDLL"] = runtimeDll;
        foreach (var argument in new[]
                 {
                     "-n",
                     "-u",
                     Identity,
                     "--",
                     "env",
                     RequiredIsolationAssignment(),
                     RequiredLeastPrivilegeAssignment(),
                     RequiredIdentityAssignment(),
                     workerPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Least Privilege Launcher did not start.");
    }

    private static async Task<LauncherResult> RunLauncherCommandAsync(params string[] arguments)
    {
        var launcherPath = Path.Combine(
            LauncherBundleRoot(),
            "OpenLineOps.LeastPrivilegeLauncher.exe");
        Assert.True(File.Exists(launcherPath), $"Missing test launcher: {launcherPath}");
        var startInfo = new ProcessStartInfo(launcherPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Least Privilege Launcher did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }
        return new LauncherResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertReadExecuteRule(
        string path,
        SecurityIdentifier capabilitySid,
        bool requireInheritance)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        var matchingRule = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .FirstOrDefault(rule =>
                capabilitySid.Equals(rule.IdentityReference)
                && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.ReadAndExecute)
                == FileSystemRights.ReadAndExecute
                && (!requireInheritance
                    || ((rule.InheritanceFlags & InheritanceFlags.ObjectInherit) != 0
                        && (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0)));
        Assert.NotNull(matchingRule);
    }

    private static void CreateDirectoryJunction(string path, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "/d",
                "/c",
                "mklink",
                "/J",
                path,
                targetPath
            }
        }) ?? throw new InvalidOperationException(
            "Failed to start the Windows junction command.");
        process.WaitForExit();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(
            process.ExitCode == 0,
            "Failed to create test junction. stdout: "
            + standardOutput
            + " stderr: "
            + standardError);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertCopiedWorkerPayload(
        string contentRoot,
        string sourceRoot)
    {
        Assert.True(File.Exists(Path.Combine(
            contentRoot,
            "OpenLineOps.ScriptWorker.exe")));

        var runtimeConfig = Path.Combine(
            sourceRoot,
            "OpenLineOps.ScriptWorker.runtimeconfig.json");
        if (File.Exists(runtimeConfig))
        {
            Assert.True(File.Exists(Path.Combine(
                contentRoot,
                "OpenLineOps.ScriptWorker.dll")));
            Assert.True(File.Exists(Path.Combine(contentRoot, "PythonScript.dll")));
            Assert.True(File.Exists(Path.Combine(
                contentRoot,
                "OpenLineOps.ScriptWorker.deps.json")));
            Assert.True(File.Exists(Path.Combine(
                contentRoot,
                "OpenLineOps.ScriptWorker.runtimeconfig.json")));
            return;
        }

        var copiedFiles = Directory
            .EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path
                .GetRelativePath(contentRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["OpenLineOps.ScriptWorker.exe"],
            copiedFiles);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<RunningLauncher> StartAndCaptureProfileAsync(
        IReadOnlySet<string> existingMarkers,
        string trustedParent)
    {
        var process = StartLauncher();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            do
            {
                var newMarkers = ExistingMarkers(trustedParent)
                    .Except(existingMarkers, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (newMarkers.Length == 1)
                {
                    var marker = await TryReadMarkerAsync(newMarkers[0]);
                    if (marker is not null
                        && Directory.Exists(marker.RuntimeRoot)
                        && WindowsAppContainerIdentity.ProfileExists(marker.ProfileName))
                    {
                        return new RunningLauncher(
                            process,
                            standardOutput,
                            standardError,
                            newMarkers[0],
                            marker.ProfileName,
                            marker.AppContainerSid,
                            marker.RuntimeRoot);
                    }
                }
                if (newMarkers.Length > 1)
                {
                    throw new InvalidDataException(
                        "More than one AppContainer marker appeared during one launch.");
                }
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        "Least Privilege Launcher exited before its AppContainer was ready: "
                        + await standardError);
                }
                await Task.Delay(50);
            }
            while (DateTimeOffset.UtcNow < deadline);

            throw new TimeoutException("The Script Worker AppContainer did not become ready.");
        }
        catch
        {
            await StopLauncherAsync(new RunningLauncher(
                process,
                standardOutput,
                standardError,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty));
            throw;
        }
    }

    private static async Task<ActiveProfileMarker?> TryReadMarkerAsync(string markerPath)
    {
        try
        {
            await using var stream = new FileStream(
                markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4_096,
                FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<ActiveProfileMarker>(
                stream,
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static HashSet<string> ExistingMarkers(string trustedParent)
    {
        if (!Directory.Exists(trustedParent))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory
            .EnumerateFiles(
                trustedParent,
                $"{ProfilePrefix}*{MarkerExtension}",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string RuntimeParentPath()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "OpenLineOps", "ScriptWorker");
    }

    [SupportedOSPlatform("windows")]
    private static void SimulateInterruptedMarkerParentInitialization(string trustedParent)
    {
        if (Directory.Exists(trustedParent))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(trustedParent));
            Directory.Delete(trustedParent, recursive: false);
        }

        Directory.CreateDirectory(trustedParent);
        var currentUser = WindowsIdentity.GetCurrent().User
                          ?? throw new InvalidDataException(
                              "The test host does not expose a Windows user SID.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(
            new DirectoryInfo(trustedParent),
            security);
    }

    private static PythonScriptExecutionScopeRequest CreateIsolationAttackRequest(
        string targetRuntimeRoot,
        int listenerPort)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                TargetRuntimeRoot = targetRuntimeRoot,
                ListenerPort = listenerPort,
                PythonRuntimeCapabilitySid =
                    WindowsAppContainerIdentity.EnsureCapabilitySid(
                        WindowsAppContainerIdentity.PythonRuntimeCapabilityName),
                InternetClientCapabilitySid =
                    WindowsAppContainerIdentity.EnsureCapabilitySid("internetClient")
            },
            JsonOptions);
        return new PythonScriptExecutionScopeRequest(
            "Python",
            """
            import ctypes
            import ctypes.wintypes
            import json
            import os
            import pathlib
            import socket
            import stat
            import subprocess
            import time

            parameters = json.loads(input_payload)
            target = pathlib.Path(parameters['targetRuntimeRoot'])
            own = pathlib.Path(os.environ['OPENLINEOPS_SCRIPT_WORKER_RUNTIME_ROOT'])

            def attempt(action):
                try:
                    action()
                    return {'allowed': True, 'native_error': 0}
                except OSError as error:
                    native_error = getattr(error, 'winerror', None)
                    if native_error is None:
                        native_error = getattr(error, 'errno', -1)
                    return {'allowed': False, 'native_error': int(native_error)}

            operations = {
                'read_peer': attempt(lambda: (target / 'host-anchor.txt').read_text()),
                'write_peer': attempt(lambda: (target / 'peer-write.txt').write_text('forbidden')),
                'delete_peer': attempt(lambda: (target / 'host-anchor.txt').unlink()),
                'rename_peer': attempt(lambda: target.rename(target.parent / (target.name + '.peer'))),
                'write_parent': attempt(lambda: (target.parent / ('peer-parent-' + command_id)).mkdir()),
            }

            set_security = ctypes.WinDLL('advapi32', use_last_error=True).SetNamedSecurityInfoW
            set_security.argtypes = [
                ctypes.c_wchar_p,
                ctypes.c_int,
                ctypes.c_uint,
                ctypes.c_void_p,
                ctypes.c_void_p,
                ctypes.c_void_p,
                ctypes.c_void_p,
            ]
            set_security.restype = ctypes.c_uint
            acl_change_result = int(set_security(str(target), 1, 4, None, None, None, None))

            advapi = ctypes.WinDLL('advapi32', use_last_error=True)
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.GetCurrentProcess.argtypes = []
            kernel.GetCurrentProcess.restype = ctypes.wintypes.HANDLE
            advapi.OpenProcessToken.argtypes = [
                ctypes.wintypes.HANDLE,
                ctypes.wintypes.DWORD,
                ctypes.POINTER(ctypes.wintypes.HANDLE),
            ]
            advapi.OpenProcessToken.restype = ctypes.wintypes.BOOL
            advapi.GetTokenInformation.argtypes = [
                ctypes.wintypes.HANDLE,
                ctypes.c_int,
                ctypes.c_void_p,
                ctypes.wintypes.DWORD,
                ctypes.POINTER(ctypes.wintypes.DWORD),
            ]
            advapi.GetTokenInformation.restype = ctypes.wintypes.BOOL
            advapi.ConvertSidToStringSidW.argtypes = [
                ctypes.c_void_p,
                ctypes.POINTER(ctypes.c_wchar_p),
            ]
            advapi.ConvertSidToStringSidW.restype = ctypes.wintypes.BOOL
            advapi.GetSidSubAuthorityCount.argtypes = [ctypes.c_void_p]
            advapi.GetSidSubAuthorityCount.restype = ctypes.POINTER(ctypes.c_ubyte)
            advapi.GetSidSubAuthority.argtypes = [
                ctypes.c_void_p,
                ctypes.wintypes.DWORD,
            ]
            advapi.GetSidSubAuthority.restype = ctypes.POINTER(ctypes.wintypes.DWORD)
            kernel.LocalFree.argtypes = [ctypes.c_void_p]
            kernel.LocalFree.restype = ctypes.c_void_p
            kernel.CloseHandle.argtypes = [ctypes.wintypes.HANDLE]
            kernel.CloseHandle.restype = ctypes.wintypes.BOOL

            process_handle = kernel.GetCurrentProcess()
            token_handle = ctypes.wintypes.HANDLE()
            if not advapi.OpenProcessToken(
                process_handle,
                0x0008,
                ctypes.byref(token_handle),
            ):
                raise ctypes.WinError(ctypes.get_last_error())

            def token_buffer(information_class):
                required = ctypes.wintypes.DWORD()
                advapi.GetTokenInformation(
                    token_handle,
                    information_class,
                    None,
                    0,
                    ctypes.byref(required),
                )
                buffer = ctypes.create_string_buffer(required.value)
                if not advapi.GetTokenInformation(
                    token_handle,
                    information_class,
                    buffer,
                    required,
                    ctypes.byref(required),
                ):
                    raise ctypes.WinError(ctypes.get_last_error())
                return buffer

            class SID_AND_ATTRIBUTES(ctypes.Structure):
                _fields_ = [
                    ('sid', ctypes.c_void_p),
                    ('attributes', ctypes.wintypes.DWORD),
                ]

            def sid_to_string(sid_pointer):
                sid_string_pointer = ctypes.c_wchar_p()
                if not advapi.ConvertSidToStringSidW(
                    sid_pointer,
                    ctypes.byref(sid_string_pointer),
                ):
                    raise ctypes.WinError(ctypes.get_last_error())
                try:
                    return sid_string_pointer.value
                finally:
                    kernel.LocalFree(sid_string_pointer)

            is_app_container_buffer = token_buffer(29)
            token_is_app_container = bool(
                ctypes.cast(is_app_container_buffer, ctypes.POINTER(ctypes.wintypes.DWORD)).contents.value
            )

            app_container_buffer = token_buffer(31)
            app_container_sid_pointer = ctypes.cast(
                app_container_buffer,
                ctypes.POINTER(ctypes.c_void_p),
            ).contents.value
            app_container_sid = sid_to_string(app_container_sid_pointer)

            capabilities_buffer = token_buffer(30)
            capability_count = ctypes.cast(
                capabilities_buffer,
                ctypes.POINTER(ctypes.wintypes.DWORD),
            ).contents.value
            pointer_size = ctypes.sizeof(ctypes.c_void_p)
            capabilities_offset = (
                ctypes.sizeof(ctypes.wintypes.DWORD) + pointer_size - 1
            ) & ~(pointer_size - 1)
            capability_sids = []
            for capability_index in range(capability_count):
                capability = ctypes.cast(
                    ctypes.addressof(capabilities_buffer)
                    + capabilities_offset
                    + capability_index * ctypes.sizeof(SID_AND_ATTRIBUTES),
                    ctypes.POINTER(SID_AND_ATTRIBUTES),
                ).contents
                capability_sids.append(sid_to_string(capability.sid))
            python_runtime_capability_present = (
                parameters['pythonRuntimeCapabilitySid'] in capability_sids
            )
            internet_client_capability_present = (
                parameters['internetClientCapabilitySid'] in capability_sids
            )

            integrity_buffer = token_buffer(25)
            integrity_sid_pointer = ctypes.cast(
                integrity_buffer,
                ctypes.POINTER(ctypes.c_void_p),
            ).contents.value
            sub_authority_count = advapi.GetSidSubAuthorityCount(integrity_sid_pointer).contents.value
            integrity_rid = int(
                advapi.GetSidSubAuthority(
                    integrity_sid_pointer,
                    sub_authority_count - 1,
                ).contents.value
            )
            kernel.CloseHandle(token_handle)

            network_connect_allowed = True
            network_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            network_socket.settimeout(1)
            try:
                network_socket.connect(('127.0.0.1', int(parameters['listenerPort'])))
            except OSError:
                network_connect_allowed = False
            finally:
                network_socket.close()

            own_file = own / 'own-write.txt'
            own_file.write_text('allowed')
            readonly_directory = own / 'readonly-tree'
            readonly_directory.mkdir()
            readonly_file = readonly_directory / 'readonly.txt'
            readonly_file.write_text('cleanup')
            os.chmod(readonly_file, stat.S_IREAD)

            deep_directory = own / 'deep-tree'
            deep_directory.mkdir()
            for _ in range(48):
                deep_directory = deep_directory / 'd'
                deep_directory.mkdir()
            wide_directory = own / 'wide-tree'
            wide_directory.mkdir()
            for index in range(512):
                (wide_directory / f'{index:04d}.txt').write_text('cleanup')

            child = subprocess.Popen([
                str(pathlib.Path(os.environ['SystemRoot']) / 'System32' / 'WindowsPowerShell' / 'v1.0' / 'powershell.exe'),
                '-NoProfile',
                '-NonInteractive',
                '-Command',
                'Start-Sleep -Seconds 60',
            ])
            time.sleep(0.25)
            child_was_running = child.poll() is None
            result = {
                'token_is_app_container': token_is_app_container,
                'app_container_sid': app_container_sid,
                'integrity_rid': integrity_rid,
                'network_connect_allowed': network_connect_allowed,
                'python_runtime_capability_present': python_runtime_capability_present,
                'internet_client_capability_present': internet_client_capability_present,
                'runtime_root': str(own),
                'worker_path': os.environ['PATH'],
                'operations': operations,
                'acl_change_result': acl_change_result,
                'own_write': own_file.exists(),
                'readonly_file_created': readonly_file.exists(),
                'deep_tree_depth': 48,
                'wide_file_count': len(list(wide_directory.iterdir())),
                'child_pid': child.pid,
                'child_was_running': child_was_running,
            }
            """,
            "appcontainer-isolation-contract",
            payload,
            "{}",
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            "line",
            "operation",
            1,
            "station",
            "product-model",
            "serial-number",
            "unit-1",
            "configuration",
            "project",
            "application",
            "snapshot",
            "node",
            Guid.NewGuid().ToString("D"),
            "action",
            "PythonScript",
            "System",
            "system",
            "Execute");
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            await Task.Delay(50);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail($"Script Worker descendant process {processId} remained alive.");
    }

    private static async Task StopLauncherAsync(RunningLauncher launcher)
    {
        try
        {
            launcher.Process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
        }

        if (!launcher.Process.HasExited)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await launcher.Process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                launcher.Process.Kill(entireProcessTree: true);
                await launcher.Process.WaitForExitAsync();
            }
        }
        launcher.Process.Dispose();
    }

    private static string WorkerPath() => Path.Combine(
        LauncherBundleRoot(),
        "OpenLineOps.ScriptWorker.exe");

    private static string LauncherBundleRoot()
    {
        var stagedBundleRoot = Environment.GetEnvironmentVariable(
            "OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT");
        if (string.IsNullOrWhiteSpace(stagedBundleRoot))
        {
            return Path.Combine(
                AppContext.BaseDirectory,
                "least-privilege-launcher-bundle");
        }
        if (char.IsWhiteSpace(stagedBundleRoot[0])
            || char.IsWhiteSpace(stagedBundleRoot[^1])
            || !Path.IsPathFullyQualified(stagedBundleRoot))
        {
            throw new InvalidDataException(
                "OPENLINEOPS_STAGED_AGENT_BUNDLE_ROOT must be a canonical absolute path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedBundleRoot));
    }

    private static string RequiredIsolationAssignment() =>
        "OPENLINEOPS_SCRIPT_WORKER_SANDBOX_ISOLATION_MODE=LeastPrivilegeIdentity";

    private static string RequiredLeastPrivilegeAssignment() =>
        "OPENLINEOPS_SCRIPT_WORKER_SANDBOX_REQUIRE_LEAST_PRIVILEGE=True";

    private static string RequiredIdentityAssignment() =>
        $"OPENLINEOPS_SCRIPT_WORKER_SANDBOX_IDENTITY={Identity}";

    private sealed record LauncherResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record RunningLauncher(
        Process Process,
        Task<string> StandardOutput,
        Task<string> StandardError,
        string MarkerPath,
        string ProfileName,
        string AppContainerSid,
        string RuntimeRoot);

    private sealed record ActiveProfileMarker(
        string ProfileName,
        string AppContainerSid,
        string RuntimeRoot);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LeastPrivilegeLauncherTestGroup
{
    public const string Name = "Least Privilege Launcher";
}
