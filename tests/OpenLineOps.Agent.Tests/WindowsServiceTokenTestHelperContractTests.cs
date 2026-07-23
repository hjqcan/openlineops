using System.Diagnostics;
using System.IO.Pipes;
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
    public void HelperBundleInventoryRejectsMutationAndUnexpectedFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "openlineops-helper-inventory-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var mutated = Path.Combine(root, "mutated");
        var extended = Path.Combine(root, "extended");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "runtime"));
            File.WriteAllText(
                Path.Combine(source, "helper.exe"),
                "entry",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(source, "runtime", "dependency.dll"),
                "dependency",
                Encoding.UTF8);

            var mutatedInventory = WindowsServiceTokenTestBridge.CopyHelperBundle(
                source,
                mutated);
            WindowsServiceTokenTestBridge.VerifyHelperBundle(
                mutated,
                mutatedInventory);
            File.AppendAllText(
                Path.Combine(mutated, "runtime", "dependency.dll"),
                "changed",
                Encoding.UTF8);
            Assert.Throws<InvalidDataException>(() =>
                WindowsServiceTokenTestBridge.VerifyHelperBundle(
                    mutated,
                    mutatedInventory));

            var extendedInventory = WindowsServiceTokenTestBridge.CopyHelperBundle(
                source,
                extended);
            File.WriteAllText(
                Path.Combine(extended, "version.dll"),
                "unexpected",
                Encoding.UTF8);
            Assert.Throws<InvalidDataException>(() =>
                WindowsServiceTokenTestBridge.VerifyHelperBundle(
                    extended,
                    extendedInventory));
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
    public void SourceProcessAccessLeaseGrantsOnlyRelayCreationAndRestoresDacl()
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

        using (var lease = WindowsProcessAccessLease.Prepare(
                   process.SafeHandle,
                   processId,
                   createdAtUtcTicks,
                   bridgeServiceSid))
        {
            lease.ApplyRequired();
            Assert.True(
                WindowsProcessAccessLease.HasExactRelayCreationAce(
                    process.SafeHandle,
                    bridgeServiceSid));
            Assert.NotEqual(
                before,
                WindowsProcessAccessLease.ReadDaclSddl(process.SafeHandle));
        }

        Assert.Equal(before, WindowsProcessAccessLease.ReadDaclSddl(process.SafeHandle));
        Assert.False(
            WindowsProcessAccessLease.HasExactRelayCreationAce(
                process.SafeHandle,
                bridgeServiceSid));
    }

    [Fact]
    public void KernelObjectGrantLeadsAnExistingBroadDenyWithoutChangingIt()
    {
        const int requestedAccess = 0x00000080;
        var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var localSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var helperSid = new SecurityIdentifier(
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                "OpenLineOpsTokenBridge-" + Guid.NewGuid().ToString("N")));
        var original = new RawAcl(GenericAcl.AclRevision, capacity: 2);
        original.InsertAce(0, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessDenied,
            unchecked((int)0x10000000),
            worldSid,
            isCallback: false,
            opaque: null));
        original.InsertAce(1, new CommonAce(
            AceFlags.Inherited,
            AceQualifier.AccessAllowed,
            requestedAccess,
            localSystemSid,
            isCallback: false,
            opaque: null));
        var originalBytes = SerializeAcl(original);

        var updated = WindowsKernelObjectAccessLease.BuildTemporaryDacl(
            original,
            helperSid,
            requestedAccess);

        var grant = Assert.IsType<CommonAce>(updated[0]);
        Assert.Equal(AceQualifier.AccessAllowed, grant.AceQualifier);
        Assert.Equal(requestedAccess, grant.AccessMask);
        Assert.Equal(helperSid, grant.SecurityIdentifier);
        Assert.Equal(original.Count + 1, updated.Count);
        Assert.Equal(
            originalBytes,
            SerializeAclWithoutFirstAce(updated));
    }

    [Fact]
    public void OverlappingKernelObjectLeasesRemoveOnlyTheirOwnSidAndRejectDaclDrift()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        var processId = checked((uint)process.Id);
        var createdAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        var firstSid = new SecurityIdentifier(
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                "OpenLineOpsTokenBridge-" + Guid.NewGuid().ToString("N")));
        var secondSid = new SecurityIdentifier(
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                "OpenLineOpsTokenBridge-" + Guid.NewGuid().ToString("N")));
        var before = WindowsProcessAccessLease.ReadDaclSddl(process.SafeHandle);
        WindowsProcessAccessLease? first = null;
        WindowsProcessAccessLease? second = null;

        try
        {
            first = WindowsProcessAccessLease.Prepare(
                process.SafeHandle,
                processId,
                createdAtUtcTicks,
                firstSid);
            first.ApplyRequired();
            second = WindowsProcessAccessLease.Prepare(
                process.SafeHandle,
                processId,
                createdAtUtcTicks,
                secondSid);
            second.ApplyRequired();
            Assert.IsType<InvalidDataException>(Record.Exception(first.Dispose));
            Assert.False(
                WindowsProcessAccessLease.HasExactRelayCreationAce(
                    process.SafeHandle,
                    firstSid));
            Assert.True(
                WindowsProcessAccessLease.HasExactRelayCreationAce(
                    process.SafeHandle,
                    secondSid));

            Assert.IsType<InvalidDataException>(Record.Exception(second.Dispose));
            Assert.Equal(before, WindowsProcessAccessLease.ReadDaclSddl(process.SafeHandle));
        }
        finally
        {
            if (second is not null)
            {
                _ = Record.Exception(second.Dispose);
            }
            if (first is not null)
            {
                _ = Record.Exception(first.Dispose);
            }
        }
    }

    [Fact]
    public void BridgeResultProtocolRejectsDuplicateUnknownAndInconsistentFields()
    {
        const string nonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const uint processId = 42;
        var valid = SuccessResultDocument(nonce, processId);

        WindowsServiceTokenTestBridge.ValidateResultDocument(
            valid,
            nonce,
            processId);
        var validText = Encoding.UTF8.GetString(valid);
        Assert.Throws<JsonException>(() =>
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                Encoding.UTF8.GetBytes(validText.Replace(
                    "\"failurePhase\":\"none\"",
                    "\"failurePhase\":\"none\",\"failurePhase\":\"none\"",
                    StringComparison.Ordinal)),
                nonce,
                processId));
        Assert.Throws<JsonException>(() =>
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                Encoding.UTF8.GetBytes(validText.Insert(1, "\"success\":true,")),
                nonce,
                processId));

        foreach (var invalid in new[]
                 {
                     MutateResult(valid, result => result["failurePhase"] = "source-process"),
                     MutateResult(valid, result => result["receiptReceived"] = false),
                     MutateResult(valid, result => result["relayProcessId"] = 0u),
                     MutateResult(valid, result =>
                         result["relayProcessCreatedAtUtcTicks"] = 0L),
                     MutateResult(valid, result =>
                         result["relayProcessCreatedAtUtcTicks"] = DateTime.MaxValue.Ticks + 1L),
                     MutateResult(valid, result => result["relayProcessValidated"] = false),
                     MutateResult(valid, result => result["sourceProcessId"] = processId + 1)
                 })
        {
            Assert.Throws<InvalidDataException>(() =>
                WindowsServiceTokenTestBridge.ValidateResultDocument(
                    invalid,
                    nonce,
                    processId));
        }

        var createFailure = RelayFailureResultDocument(
            nonce,
            processId,
            "create-source-token-relay",
            relayWasCreated: false);
        WindowsServiceTokenTestBridge.ValidateResultDocument(
            createFailure,
            nonce,
            processId);
        Assert.Throws<InvalidDataException>(() =>
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                MutateResult(createFailure, result =>
                {
                    result["relayProcessId"] = 43u;
                    result["relayProcessCreatedAtUtcTicks"] = 638000000000000000L;
                    result["relayProcessValidated"] = true;
                }),
                nonce,
                processId));

        var postCreateFailure = RelayFailureResultDocument(
            nonce,
            processId,
            "source-token-relay-exit",
            relayWasCreated: true);
        WindowsServiceTokenTestBridge.ValidateResultDocument(
            postCreateFailure,
            nonce,
            processId);
        Assert.Throws<InvalidDataException>(() =>
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                MutateResult(postCreateFailure, result =>
                {
                    result["relayProcessId"] = 0u;
                    result["relayProcessCreatedAtUtcTicks"] = 0L;
                    result["relayProcessValidated"] = false;
                }),
                nonce,
                processId));

        foreach (var completedBeforeCleanupFailure in new[] { false, true })
        {
            var cleanupFailure = ResultDocument(nonce, processId, result =>
            {
                result["sourceTokenValidated"] = completedBeforeCleanupFailure;
                result["controlPipeConnected"] = completedBeforeCleanupFailure;
                result["receiptReceived"] = completedBeforeCleanupFailure;
                result["failurePhase"] = "source-relay-cleanup";
                result["failureReason"] = "terminate-job-and-wait";
                result["win32Error"] = 5;
            });
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                cleanupFailure,
                nonce,
                processId);
            Assert.Throws<InvalidDataException>(() =>
                WindowsServiceTokenTestBridge.ValidateResultDocument(
                    MutateResult(cleanupFailure, result =>
                        result["failureReason"] = "source-token-relay-exit"),
                    nonce,
                    processId));
            Assert.Throws<InvalidDataException>(() =>
                WindowsServiceTokenTestBridge.ValidateResultDocument(
                    MutateResult(cleanupFailure, result =>
                        result["receiptReceived"] = !completedBeforeCleanupFailure),
                    nonce,
                    processId));
        }

        var reportedBeforeBindingCleanupFailure = ResultDocument(
            nonce,
            processId,
            result =>
            {
                result["relayProcessCreatedAtUtcTicks"] = 0L;
                result["relayProcessValidated"] = false;
                result["sourceTokenValidated"] = false;
                result["controlPipeConnected"] = false;
                result["receiptReceived"] = false;
                result["failurePhase"] = "source-relay-cleanup";
                result["failureReason"] = "terminate-job-and-wait";
                result["win32Error"] = 5;
            });
        WindowsServiceTokenTestBridge.ValidateResultDocument(
            reportedBeforeBindingCleanupFailure,
            nonce,
            processId);
        Assert.Throws<InvalidDataException>(() =>
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                MutateResult(reportedBeforeBindingCleanupFailure, result =>
                {
                    result["sourceTokenValidated"] = true;
                    result["controlPipeConnected"] = true;
                    result["receiptReceived"] = true;
                }),
                nonce,
                processId));
        Assert.Throws<InvalidDataException>(() =>
            WindowsServiceTokenTestBridge.ValidateResultDocument(
                MutateResult(reportedBeforeBindingCleanupFailure, result =>
                    result["relayProcessValidated"] = true),
                nonce,
                processId));
    }

    [Fact]
    public void BridgeResultProtocolAcceptsEveryBoundedFailurePhaseAndReason()
    {
        const string nonce = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const uint sourceProcessId = 52;
        var contracts = new[]
        {
            new FailureContract("helper-identity", "helper-identity", false, false, false, 0, false),
            new FailureContract("runner-coordination", "connect-and-grant", true, false, false, 0, false),
            new FailureContract("source-service", "source-service", true, false, false, 0, false),
            new FailureContract("source-process", "open-process", true, true, false, 0, false),
            new FailureContract("source-process", "process-validation", true, true, false, 0, false),
            new FailureContract("source-relay", "create-source-token-relay", true, true, true, 0, false),
            new FailureContract("source-relay", "relay-observation", true, true, true, 3, false),
            new FailureContract("source-relay", "relay-creation-time", true, true, true, 3, false),
            new FailureContract("source-relay", "relay-validation", true, true, true, 1, false),
            new FailureContract("source-relay", "source-service-before-relay-ready", true, true, true, 2, false),
            new FailureContract("source-relay", "relay-ready", true, true, true, 2, false),
            new FailureContract("source-relay", "source-service-before-relay-resume", true, true, true, 2, false),
            new FailureContract("source-relay", "resume-source-token-relay", true, true, true, 2, false),
            new FailureContract("source-relay", "source-token-relay-exit", true, true, true, 2, false),
            new FailureContract("post-receipt-source", "source-service-post-receipt", true, true, true, 2, true)
        };

        foreach (var contract in contracts)
        {
            var document = ResultDocument(nonce, sourceProcessId, result =>
            {
                result["helperIdentityValidated"] = contract.HelperIdentityValidated;
                result["sourceServiceValidated"] = contract.SourceServiceValidated;
                result["sourceProcessValidated"] = contract.SourceProcessValidated;
                result["relayProcessId"] = contract.RelayState == 0 ? 0u : 43u;
                result["relayProcessCreatedAtUtcTicks"] =
                    contract.RelayState is 0 or 3
                        ? 0L
                        : 638000000000000000L;
                result["relayProcessValidated"] = contract.RelayState == 2;
                result["sourceTokenValidated"] = contract.Completed;
                result["controlPipeConnected"] = contract.Completed;
                result["receiptReceived"] = contract.Completed;
                result["failurePhase"] = contract.Phase;
                result["failureReason"] = contract.Reason;
                result["win32Error"] = 5;
            });

            WindowsServiceTokenTestBridge.ValidateResultDocument(
                document,
                nonce,
                sourceProcessId);
        }

        foreach (var reason in new[] { "relay-observation", "relay-creation-time" })
        {
            var unreachableCreatedAtResult = ResultDocument(
                nonce,
                sourceProcessId,
                result =>
                {
                    result["relayProcessValidated"] = false;
                    result["sourceTokenValidated"] = false;
                    result["controlPipeConnected"] = false;
                    result["receiptReceived"] = false;
                    result["failurePhase"] = "source-relay";
                    result["failureReason"] = reason;
                    result["win32Error"] = 5;
                });
            Assert.Throws<InvalidDataException>(() =>
                WindowsServiceTokenTestBridge.ValidateResultDocument(
                    unreachableCreatedAtResult,
                    nonce,
                    sourceProcessId));
        }
    }

    [Theory]
    [InlineData(44u, 638000000000000000L)]
    [InlineData(43u, 638000000000000001L)]
    public void CapturedRelayBindingRejectsMismatchedPidOrCreationTime(
        uint resultProcessId,
        long resultCreatedAtUtcTicks)
    {
        Assert.True(WindowsServiceTokenTestBridge.HasCapturedRelayBinding(
            preparedProcessId: 43u,
            preparedCreatedAtUtcTicks: 638000000000000000L,
            pipeClientProcessId: 43u,
            retainedProcessId: 43u,
            retainedCreatedAtUtcTicks: 638000000000000000L));
        Assert.False(WindowsServiceTokenTestBridge.HasCapturedRelayBinding(
            preparedProcessId: resultProcessId,
            preparedCreatedAtUtcTicks: resultCreatedAtUtcTicks,
            pipeClientProcessId: 43u,
            retainedProcessId: 43u,
            retainedCreatedAtUtcTicks: 638000000000000000L));
    }

    [Theory]
    [InlineData(44u, 638000000000000000L)]
    [InlineData(43u, 638000000000000001L)]
    public void ResultRelayBindingRejectsMismatchedPidOrCreationTime(
        uint resultProcessId,
        long resultCreatedAtUtcTicks)
    {
        Assert.True(WindowsServiceTokenTestBridge.HasResultRelayBinding(
            capturedProcessId: 43u,
            capturedCreatedAtUtcTicks: 638000000000000000L,
            resultProcessId: 43u,
            resultCreatedAtUtcTicks: 638000000000000000L));
        Assert.False(WindowsServiceTokenTestBridge.HasResultRelayBinding(
            capturedProcessId: 43u,
            capturedCreatedAtUtcTicks: 638000000000000000L,
            resultProcessId,
            resultCreatedAtUtcTicks));
    }

    [Fact]
    public void FailureResultRelayBindingAllowsPidOnlyBeforeRelayReady()
    {
        const uint processId = 43;
        const long createdAtUtcTicks = 638000000000000000L;

        Assert.True(WindowsServiceTokenTestBridge.HasFailureResultRelayBinding(
            processId,
            createdAtUtcTicks,
            relayReadyReceived: false,
            processId,
            resultCreatedAtUtcTicks: 0,
            resultValidated: false));
        Assert.False(WindowsServiceTokenTestBridge.HasFailureResultRelayBinding(
            processId,
            createdAtUtcTicks,
            relayReadyReceived: true,
            processId,
            resultCreatedAtUtcTicks: 0,
            resultValidated: false));
        Assert.True(WindowsServiceTokenTestBridge.HasFailureResultRelayBinding(
            processId,
            createdAtUtcTicks,
            relayReadyReceived: true,
            processId,
            createdAtUtcTicks,
            resultValidated: false));
        Assert.False(WindowsServiceTokenTestBridge.HasFailureResultRelayBinding(
            processId,
            createdAtUtcTicks,
            relayReadyReceived: false,
            resultProcessId: processId + 1,
            resultCreatedAtUtcTicks: 0,
            resultValidated: false));
    }

    [Fact]
    public void AuthenticatedPipeClientRightsExcludeAdministrativeAndInstanceCreationRights()
    {
        var rights = WindowsServiceTokenTestBridge.AuthenticatedPipeClientRights;
        Assert.Equal(
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            rights);
        Assert.NotEqual(PipeAccessRights.FullControl, rights);
        Assert.Equal(
            (PipeAccessRights)0,
            rights & (PipeAccessRights.ChangePermissions
                      | PipeAccessRights.TakeOwnership
                      | PipeAccessRights.Delete
                      | PipeAccessRights.CreateNewInstance));
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
            WindowsProcessAccessLease.Prepare(
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

    private static byte[] SerializeAcl(RawAcl acl)
    {
        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static byte[] SerializeAclWithoutFirstAce(RawAcl acl)
    {
        var retained = new RawAcl(acl.Revision, acl.Count - 1);
        for (var index = 1; index < acl.Count; index++)
        {
            var aceBytes = new byte[acl[index].BinaryLength];
            acl[index].GetBinaryForm(aceBytes, 0);
            retained.InsertAce(
                index - 1,
                GenericAce.CreateFromBinaryForm(aceBytes, 0));
        }

        return SerializeAcl(retained);
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

    private static byte[] SuccessResultDocument(string nonce, uint sourceProcessId) =>
        ResultDocument(nonce, sourceProcessId, result => { });

    private sealed record FailureContract(
        string Phase,
        string Reason,
        bool HelperIdentityValidated,
        bool SourceServiceValidated,
        bool SourceProcessValidated,
        int RelayState,
        bool Completed);

    private static byte[] RelayFailureResultDocument(
        string nonce,
        uint sourceProcessId,
        string failureReason,
        bool relayWasCreated) =>
        ResultDocument(nonce, sourceProcessId, result =>
        {
            result["relayProcessId"] = relayWasCreated ? 43u : 0u;
            result["relayProcessCreatedAtUtcTicks"] = relayWasCreated
                ? 638000000000000000L
                : 0L;
            result["relayProcessValidated"] = relayWasCreated;
            result["sourceTokenValidated"] = false;
            result["controlPipeConnected"] = false;
            result["receiptReceived"] = false;
            result["failurePhase"] = "source-relay";
            result["failureReason"] = failureReason;
        });

    private static byte[] MutateResult(
        byte[] document,
        Action<Dictionary<string, object?>> mutation)
    {
        var result = JsonSerializer.Deserialize<Dictionary<string, object?>>(document)
                     ?? throw new InvalidDataException("The result fixture is null.");
        mutation(result);
        return JsonSerializer.SerializeToUtf8Bytes(result);
    }

    private static byte[] ResultDocument(
        string nonce,
        uint sourceProcessId,
        Action<Dictionary<string, object?>> mutation)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["nonce"] = nonce,
            ["sourceProcessId"] = sourceProcessId,
            ["relayProcessId"] = 43u,
            ["relayProcessCreatedAtUtcTicks"] = 638000000000000000L,
            ["helperIdentityValidated"] = true,
            ["sourceServiceValidated"] = true,
            ["sourceProcessValidated"] = true,
            ["relayProcessValidated"] = true,
            ["sourceTokenValidated"] = true,
            ["controlPipeConnected"] = true,
            ["receiptReceived"] = true,
            ["failurePhase"] = "none",
            ["failureReason"] = "none",
            ["win32Error"] = 0
        };
        mutation(result);
        return JsonSerializer.SerializeToUtf8Bytes(result);
    }

}
