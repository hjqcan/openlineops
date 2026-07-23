using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.ContentProtection;

namespace OpenLineOps.Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceTokenTestRelayContractTests
{
    private static readonly JsonSerializerOptions RelayJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    [Fact]
    public void RelayBundleCopyIsFrozenAndRejectsChangedOrAddedFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        File.WriteAllText(Path.Combine(source, "relay.exe"), "relay");
        File.WriteAllText(Path.Combine(nested, "runtime.dll"), "runtime");
        var destination = Path.Combine(root.Path, "destination");

        var inventory = WindowsServiceTokenTestBridge.CopyRelayBundle(
            source,
            destination);
        WindowsServiceTokenTestBridge.VerifyRelayBundle(destination, inventory);

        File.AppendAllText(Path.Combine(destination, "relay.exe"), "-changed");
        Assert.Throws<InvalidDataException>(
            () => WindowsServiceTokenTestBridge.VerifyRelayBundle(
                destination,
                inventory));

        Directory.Delete(destination, recursive: true);
        inventory = WindowsServiceTokenTestBridge.CopyRelayBundle(
            source,
            destination);
        File.WriteAllText(Path.Combine(destination, "unexpected.dll"), "unexpected");
        Assert.Throws<InvalidDataException>(
            () => WindowsServiceTokenTestBridge.VerifyRelayBundle(
                destination,
                inventory));
    }

    [Fact]
    public void RelayBundleCopyRejectsAnEmptyBundle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        var destination = Path.Combine(root.Path, "destination");

        Assert.Throws<InvalidDataException>(
            () => WindowsServiceTokenTestBridge.CopyRelayBundle(
                source,
                destination));
    }

    [Fact]
    public void RelayTreeOwnerCanonicalizationRestoresEveryEntry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var child = Directory.CreateDirectory(Path.Combine(root.Path, "child"));
        var file = Path.Combine(child.FullName, "payload.bin");
        File.WriteAllText(file, "payload");

        WindowsServiceTokenTestBridge.CanonicalizeRelayTreeOwner(root.Path);

        var expected = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(expected);
        Assert.Equal(expected, ReadOwner(new DirectoryInfo(root.Path)));
        Assert.Equal(expected, ReadOwner(child));
        Assert.Equal(expected, ReadOwner(new FileInfo(file)));
    }

    [Fact]
    public void ExactProcessHandleRejectsPidAndCreationTimeDrift()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        var pid = checked((uint)process.Id);
        var createdAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;

        WindowsServiceTokenTestBridge.ValidateExactProcessHandle(
            process.SafeHandle,
            pid,
            createdAtUtcTicks,
            "contract process");
        Assert.Throws<InvalidDataException>(
            () => WindowsServiceTokenTestBridge.ValidateExactProcessHandle(
                process.SafeHandle,
                pid == uint.MaxValue ? pid - 1 : pid + 1,
                createdAtUtcTicks,
                "contract process"));
        Assert.Throws<InvalidDataException>(
            () => WindowsServiceTokenTestBridge.ValidateExactProcessHandle(
                process.SafeHandle,
                pid,
                createdAtUtcTicks + 1,
                "contract process"));
    }

    [Fact]
    public void PipeClientRightsContainOnlyProtocolAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            System.IO.Pipes.PipeAccessRights.ReadWrite
            | System.IO.Pipes.PipeAccessRights.Synchronize,
            WindowsServiceTokenTestBridge.AuthenticatedPipeClientRights);
        Assert.False(
            WindowsServiceTokenTestBridge.AuthenticatedPipeClientRights.HasFlag(
                System.IO.Pipes.PipeAccessRights.ChangePermissions));
        Assert.False(
            WindowsServiceTokenTestBridge.AuthenticatedPipeClientRights.HasFlag(
                System.IO.Pipes.PipeAccessRights.TakeOwnership));
    }

    [Fact]
    public void RelayExecutableRejectsEveryInvocationExceptOneCanonicalRequest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var relayExecutable = RequiredRelayExecutable();
        Assert.Equal(64, RunRelay(relayExecutable, []));
        Assert.Equal(64, RunRelay(relayExecutable, ["--unknown", relayExecutable]));
        Assert.Equal(64, RunRelay(relayExecutable, ["--request"]));
        Assert.Equal(
            64,
            RunRelay(
                relayExecutable,
                ["--request", relayExecutable, "unexpected"]));
    }

    [Fact]
    public void RelayExecutableRejectsUnknownAndMissingRequestProperties()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var relayExecutable = RequiredRelayExecutable();
        var missingPath = Path.Combine(root.Path, "missing.json");
        File.WriteAllText(missingPath, "{}");
        Assert.Equal(70, RunRelay(relayExecutable, ["--request", missingPath]));

        var unknownPath = Path.Combine(root.Path, "unknown.json");
        File.WriteAllText(
            unknownPath,
            """
            {
              "nonce": "0000000000000000000000000000000000000000000000000000000000000000",
              "sourceProcessId": 1,
              "sourceProcessCreatedAtUtcTicks": 621355968000000000,
              "sourceExecutablePath": "C:\\Windows\\System32\\notepad.exe",
              "sourceExecutableSha256": "0000000000000000000000000000000000000000000000000000000000000000",
              "expectedSourceServiceSid": "S-1-5-80-1-2-3-4-5",
              "relayBundleRoot": "C:\\Windows",
              "relayExecutablePath": "C:\\Windows\\OpenLineOps.WindowsServiceToken.TestRelay.exe",
              "relayExecutableSha256": "0000000000000000000000000000000000000000000000000000000000000000",
              "controlPipeName": "openlineops-source-token-relay-contract",
              "unknown": true
            }
            """);
        Assert.Equal(70, RunRelay(relayExecutable, ["--request", unknownPath]));
    }

    [Fact]
    public void CanonicalRelayRequestReachesTokenSelfAttestation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        using var source = Process.GetCurrentProcess();
        var relayExecutable = RequiredRelayExecutable();
        var relayBundleRoot = Path.GetDirectoryName(relayExecutable)
                              ?? throw new InvalidDataException(
                                  "The staged Test Relay has no bundle root.");
        var requestPath = Path.Combine(root.Path, "request.json");
        var request = CreateControllerRequest(
            requestPath,
            relayBundleRoot,
            relayExecutable,
            source);
        WriteRelayRequest(requestPath, request);

        var outcome = RunRelayWithOutput(
            relayExecutable,
            ["--request", requestPath]);

        Assert.Equal(70, outcome.ExitCode);
        Assert.Contains(
            "exact primary, unlinked, restricted LocalService identity",
            outcome.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "canonical relay pipe name",
            outcome.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SuspendedRelayIsBoundToItsImageAndKilledByJobDisposal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var relayExecutable = RequiredRelayExecutable();
        var relayBundleRoot = Path.GetDirectoryName(relayExecutable)
                              ?? throw new InvalidDataException(
                                  "The staged Test Relay has no bundle root.");
        var requestPath = Path.Combine(root.Path, "request.json");
        File.WriteAllText(requestPath, "{}");
        using var source = Process.GetCurrentProcess();
        var request = CreateControllerRequest(
            requestPath,
            relayBundleRoot,
            relayExecutable,
            source);
        var runnerSid = WindowsIdentity.GetCurrent().User
                        ?? throw new InvalidOperationException(
                            "The relay contract runner has no SID.");
        uint relayProcessId;
        using (var relay = WindowsSourceTokenRelayProcess.CreateSuspended(
                   request,
                   source.SafeHandle,
                   runnerSid))
        {
            relayProcessId = relay.ProcessId;
            relay.ValidateCreated(request);
            relay.ValidateRunning(request);
        }

        AssertEventuallyProcessMissing(relayProcessId);
    }

    [Fact]
    public void ResumedRelayRejectsAnOrdinaryRunnerTokenBeforePipeAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var relayExecutable = RequiredRelayExecutable();
        var relayBundleRoot = Path.GetDirectoryName(relayExecutable)
                              ?? throw new InvalidDataException(
                                  "The staged Test Relay has no bundle root.");
        using var source = Process.GetCurrentProcess();
        var requestPath = Path.Combine(root.Path, "request.json");
        var request = CreateControllerRequest(
            requestPath,
            relayBundleRoot,
            relayExecutable,
            source);
        WriteRelayRequest(requestPath, request);
        var runnerSid = WindowsIdentity.GetCurrent().User
                        ?? throw new InvalidOperationException(
                            "The relay contract runner has no SID.");
        using var relay = WindowsSourceTokenRelayProcess.CreateSuspended(
            request,
            source.SafeHandle,
            runnerSid);
        relay.ValidateCreated(request);
        relay.Resume();

        var failure = Assert.Throws<InvalidOperationException>(
            () => relay.WaitForSuccessfulExit(TimeSpan.FromSeconds(15)));
        Assert.Contains("exited with code 70", failure.Message, StringComparison.Ordinal);
    }

    private static WindowsSourceTokenRelayRequest CreateControllerRequest(
        string requestPath,
        string relayBundleRoot,
        string relayExecutable,
        Process source)
    {
        var serviceName = "OpenLineOpsRelayContract";
        return new WindowsSourceTokenRelayRequest(
            requestPath,
            new string('0', 64),
            checked((uint)source.Id),
            source.StartTime.ToUniversalTime().Ticks,
            source.MainModule?.FileName
            ?? throw new InvalidDataException("The contract process image is unavailable."),
            Sha256File(
                source.MainModule?.FileName
                ?? throw new InvalidDataException(
                    "The contract process image is unavailable.")),
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(serviceName),
            relayBundleRoot,
            relayExecutable,
            Sha256File(relayExecutable),
            "openlineops-source-token-relay-contract");
    }

    private static void WriteRelayRequest(
        string path,
        WindowsSourceTokenRelayRequest request)
    {
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    request.Nonce,
                    request.SourceProcessId,
                    request.SourceProcessCreatedAtUtcTicks,
                    request.SourceExecutablePath,
                    request.SourceExecutableSha256,
                    request.ExpectedSourceServiceSid,
                    request.RelayBundleRoot,
                    request.RelayExecutablePath,
                    request.RelayExecutableSha256,
                    request.ControlPipeName
                },
                RelayJsonOptions));
    }

    private static string RequiredRelayExecutable()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "windows-service-token-test-relay",
            "OpenLineOps.WindowsServiceToken.TestRelay.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "The staged Windows service-token Test Relay executable is missing.",
                path);
    }

    private static int RunRelay(
        string relayExecutable,
        IReadOnlyList<string> arguments) =>
        RunRelayWithOutput(relayExecutable, arguments).ExitCode;

    private static RelayProcessOutcome RunRelayWithOutput(
        string relayExecutable,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = relayExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                "The Test Relay did not start.");
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The Test Relay invocation did not exit.");
        }
        return new RelayProcessOutcome(
            process.ExitCode,
            process.StandardError.ReadToEnd());
    }

    private sealed record RelayProcessOutcome(int ExitCode, string StandardError);

    private static SecurityIdentifier ReadOwner(FileSystemInfo entry)
    {
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? FileSystemAclExtensions.GetAccessControl(
                directory,
                AccessControlSections.Owner)
            : FileSystemAclExtensions.GetAccessControl(
                (FileInfo)entry,
                AccessControlSections.Owner);
        return security.GetOwner(typeof(SecurityIdentifier))
               as SecurityIdentifier
               ?? throw new InvalidDataException(
                   $"The contract entry '{entry.FullName}' has no SID owner.");
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void AssertEventuallyProcessMissing(uint processId)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            Thread.Sleep(25);
        }
        throw new InvalidOperationException(
            $"Suspended Test Relay PID {processId} survived its kill-on-close job.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "openlineops-relay-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
