using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenLineOps.ContentProtection;

namespace OpenLineOps.Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceTokenTestHelperContractTests
{
    [Fact]
    public void BridgeTreeOwnerCanonicalizationPreservesAccessRules()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "openlineops-token-owner-" + Guid.NewGuid().ToString("N"));
        var childDirectory = Path.Combine(root, "child");
        var childFile = Path.Combine(childDirectory, "request.json");
        try
        {
            Directory.CreateDirectory(childDirectory);
            var directoryAccessBefore = AccessSddl(new DirectoryInfo(childDirectory));
            WindowsServiceTokenTestBridge.CanonicalizeBridgeTreeOwner(root);
            File.WriteAllText(childFile, "{}");
            var fileAccessBefore = AccessSddl(new FileInfo(childFile));

            WindowsServiceTokenTestBridge.CanonicalizeBridgeTreeOwner(root);

            var expectedOwner = WindowsIdentity.GetCurrent().User
                                ?? throw new InvalidOperationException(
                                    "The test runner has no Windows SID.");
            Assert.Equal(expectedOwner, Owner(new DirectoryInfo(root)));
            Assert.Equal(expectedOwner, Owner(new DirectoryInfo(childDirectory)));
            Assert.Equal(expectedOwner, Owner(new FileInfo(childFile)));
            Assert.Equal(directoryAccessBefore, AccessSddl(new DirectoryInfo(childDirectory)));
            Assert.Equal(fileAccessBefore, AccessSddl(new FileInfo(childFile)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SourceProcessAccessLeaseGrantsOnlyBridgeQueryAndWaitAndRestoresDacl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        var processId = checked((uint)process.Id);
        var createdAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        var bridgeServiceSid = new SecurityIdentifier(
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                "OpenLineOpsTokenBridge-" + Guid.NewGuid().ToString("N")));
        var before = WindowsProcessAccessLease.ReadDaclSddl(process.SafeHandle);

        using (WindowsProcessAccessLease.Acquire(
                   process.SafeHandle,
                   processId,
                   createdAtUtcTicks,
                   bridgeServiceSid))
        {
            Assert.True(
                WindowsProcessAccessLease.HasExactQueryAndWaitAce(
                    process.SafeHandle,
                    bridgeServiceSid));
            Assert.NotEqual(
                before,
                WindowsProcessAccessLease.ReadDaclSddl(process.SafeHandle));
        }

        Assert.Equal(before, WindowsProcessAccessLease.ReadDaclSddl(process.SafeHandle));
        Assert.False(
            WindowsProcessAccessLease.HasExactQueryAndWaitAce(
                process.SafeHandle,
                bridgeServiceSid));
    }

    [Fact]
    public void BridgeResultProtocolRejectsDuplicateUnknownAndInconsistentFields()
    {
        const string nonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const uint processId = 42;
        const string valid =
            "{\"nonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sourceProcessId\":42,\"helperIdentityValidated\":true,\"sourceServiceValidated\":true,\"sourceProcessValidated\":true,\"sourceTokenValidated\":true,\"controlPipeConnected\":true,\"receiptReceived\":true,\"failurePhase\":\"none\",\"failureReason\":\"none\",\"win32Error\":0}";

        WindowsServiceTokenTestBridge.ValidateResultDocument(
            Encoding.UTF8.GetBytes(valid),
            nonce,
            processId);
        Assert.Throws<JsonException>(() =>
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                Encoding.UTF8.GetBytes(valid.Replace(
                    "\"failurePhase\":\"none\"",
                    "\"failurePhase\":\"none\",\"failurePhase\":\"none\"",
                    StringComparison.Ordinal)),
                nonce,
                processId));
        Assert.Throws<JsonException>(() =>
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                Encoding.UTF8.GetBytes(valid.Insert(1, "\"success\":true,")),
                nonce,
                processId));
        Assert.Throws<InvalidDataException>(() =>
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                Encoding.UTF8.GetBytes(valid.Replace(
                    "\"failurePhase\":\"none\"",
                    "\"failurePhase\":\"source-process\"",
                    StringComparison.Ordinal)),
                nonce,
                processId));
        Assert.Throws<InvalidDataException>(() =>
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                Encoding.UTF8.GetBytes(valid.Replace(
                    "\"receiptReceived\":true",
                    "\"receiptReceived\":false",
                    StringComparison.Ordinal)),
                nonce,
                processId));
    }

    [Fact]
    public void SourceProcessAccessLeaseRejectsExitedProcessWithStillActiveExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = "/d /s /c \"exit 259\"",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the exit-code contract process.");
        var createdAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        var processId = checked((uint)process.Id);
        var processHandle = process.SafeHandle;
        process.WaitForExit();
        Assert.Equal(259, process.ExitCode);
        var bridgeServiceSid = new SecurityIdentifier(
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                "OpenLineOpsTokenBridge-" + Guid.NewGuid().ToString("N")));

        Assert.Throws<InvalidOperationException>(() =>
            WindowsProcessAccessLease.Acquire(
                processHandle,
                processId,
                createdAtUtcTicks,
                bridgeServiceSid));
    }

    [Fact]
    public void HelperProcessCleanupTerminatesOnlyTheExactCapturedProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 30 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the helper-cleanup contract process.");
        try
        {
            WindowsServiceTokenTestBridge.EnsureHelperProcessTerminated(
                process.SafeHandle,
                "OpenLineOpsTokenBridge-contract");
            process.WaitForExit();
            Assert.Equal(70, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }

    [Fact]
    public async Task SelfContainedHelperRejectsInvocationOutsideFixedProtocol()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var helperRoot = Path.Combine(
            AppContext.BaseDirectory,
            "windows-service-token-test-helper");
        var helperPath = Path.Combine(
            helperRoot,
            "OpenLineOps.WindowsServiceToken.TestHelper.exe");
        Assert.True(File.Exists(helperPath), $"Missing staged helper: {helperPath}");
        Assert.True(
            File.Exists(Path.Combine(helperRoot, "coreclr.dll")),
            "The SCM test helper must be self-contained and must not depend on the runner user's .NET installation.");

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.Environment.Remove("DOTNET_ROOT");
        startInfo.Environment.Remove("DOTNET_HOST_PATH");

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                "Could not start the self-contained service-token test helper.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(64, process.ExitCode);
            Assert.Contains("accepts exactly", await standardError, StringComparison.Ordinal);
            Assert.Empty(await standardOutput);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static SecurityIdentifier? Owner(FileSystemInfo entry)
    {
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? FileSystemAclExtensions.GetAccessControl(
                directory,
                AccessControlSections.Owner)
            : FileSystemAclExtensions.GetAccessControl(
                (FileInfo)entry,
                AccessControlSections.Owner);
        return (SecurityIdentifier?)security.GetOwner(typeof(SecurityIdentifier));
    }

    private static string AccessSddl(FileSystemInfo entry)
    {
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? FileSystemAclExtensions.GetAccessControl(
                directory,
                AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(
                (FileInfo)entry,
                AccessControlSections.Access);
        return security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }
}
