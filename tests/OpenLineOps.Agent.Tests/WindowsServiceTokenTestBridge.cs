using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.ContentProtection;

namespace OpenLineOps.Agent.Tests;

[SupportedOSPlatform("windows")]
internal static class WindowsServiceTokenTestBridge
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceRunning = 0x00000004;
    private const uint ScStatusProcessInfo = 0;
    private const uint ProcessCreateProcess = 0x00000080;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private const int ErrorAlreadyExists = 183;
    private const int NonceBytes = 32;
    private const byte CompletionReceipt = 0xA5;

    internal const PipeAccessRights AuthenticatedPipeClientRights =
        PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    public static T Run<T>(
        string bridgeParentRoot,
        string sourceServiceName,
        SafeProcessHandle sourceProcessHandle,
        uint sourceProcessId,
        string sourceExecutablePath,
        string sourceExecutableSha256,
        string expectedSourceServiceSid,
        Func<T> action)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows service-token test bridging requires Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeParentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceServiceName);
        ArgumentNullException.ThrowIfNull(sourceProcessHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceExecutableSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceServiceSid);
        ArgumentNullException.ThrowIfNull(action);
        if (sourceProcessId == 0
            || sourceProcessHandle.IsInvalid
            || sourceProcessHandle.IsClosed)
        {
            throw new ArgumentException(
                "A live retained source service process is required.",
                nameof(sourceProcessHandle));
        }

        var canonicalSourceServiceName =
            WindowsStationServiceIdentityReader.RequireCanonicalServiceName(
                sourceServiceName,
                nameof(sourceServiceName));
        var canonicalSourceServiceSid =
            WindowsStationServiceIdentityReader.RequireCanonicalServiceSid(
                expectedSourceServiceSid,
                nameof(expectedSourceServiceSid));
        if (!string.Equals(
                WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                    canonicalSourceServiceName),
                canonicalSourceServiceSid,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The requested source service SID does not derive from its SCM service name.");
        }

        var fullParentRoot = Path.GetFullPath(bridgeParentRoot);
        if (!Directory.Exists(fullParentRoot))
        {
            throw new DirectoryNotFoundException(
                $"The service-token relay parent root '{fullParentRoot}' is missing.");
        }

        var fullSourceExecutablePath = Path.GetFullPath(sourceExecutablePath);
        if (!File.Exists(fullSourceExecutablePath))
        {
            throw new FileNotFoundException(
                "The source Station executable is missing.",
                fullSourceExecutablePath);
        }

        var canonicalSourceExecutableSha256 = RequireSha256(
            sourceExecutableSha256,
            nameof(sourceExecutableSha256));
        if (!string.Equals(
                canonicalSourceExecutableSha256,
                Sha256File(fullSourceExecutablePath),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The source Station executable changed before service-token bridging.");
        }

        var nonceBytes = RandomNumberGenerator.GetBytes(NonceBytes);
        var nonce = Convert.ToHexStringLower(nonceBytes);
        var controlPipeName = "openlineops-source-token-relay-" + nonce;
        var bridgeRoot = Path.Combine(
            fullParentRoot,
            ".source-token-relay-" + nonce[..16]);
        var protocolRoot = Path.Combine(bridgeRoot, "protocol");
        var requestPath = Path.Combine(protocolRoot, "request.json");
        var relayBundleSource = Path.Combine(
            AppContext.BaseDirectory,
            "windows-service-token-test-relay");
        var relayBundleRoot = Path.Combine(bridgeRoot, "relay");
        var relayExecutablePath = Path.Combine(
            relayBundleRoot,
            "OpenLineOps.WindowsServiceToken.TestRelay.exe");

        NamedPipeServerStream? controlPipe = null;
        WindowsSourceTokenRelayProcess? relay = null;
        ExceptionDispatchInfo? operationFailure = null;
        ExceptionDispatchInfo? actionFailure = null;
        var cleanupFailures = new List<Exception>();
        var bridgeRootOwned = false;
        var actionCompleted = false;
        T? actionResult = default;
        try
        {
            PrepareRelayRoot(bridgeRoot, canonicalSourceServiceSid);
            bridgeRootOwned = true;
            Directory.CreateDirectory(protocolRoot);
            var relayBundleInventory = CopyRelayBundle(
                relayBundleSource,
                relayBundleRoot);
            if (!File.Exists(relayExecutablePath))
            {
                throw new FileNotFoundException(
                    "The staged Windows service-token Test Relay executable is missing.",
                    relayExecutablePath);
            }

            var relayExecutableSha256 = Sha256File(relayExecutablePath);
            var sourceProcessCreatedAtUtcTicks = ReadProcessCreationUtcTicks(
                sourceProcessHandle);
            var request = new WindowsSourceTokenRelayRequest(
                requestPath,
                nonce,
                sourceProcessId,
                sourceProcessCreatedAtUtcTicks,
                fullSourceExecutablePath,
                canonicalSourceExecutableSha256,
                canonicalSourceServiceSid,
                relayBundleRoot,
                relayExecutablePath,
                relayExecutableSha256,
                controlPipeName);
            WriteJsonAtomically(
                requestPath,
                new RelayRequestDocument(
                    request.Nonce,
                    request.SourceProcessId,
                    request.SourceProcessCreatedAtUtcTicks,
                    request.SourceExecutablePath,
                    request.SourceExecutableSha256,
                    request.ExpectedSourceServiceSid,
                    request.RelayBundleRoot,
                    request.RelayExecutablePath,
                    request.RelayExecutableSha256,
                    request.ControlPipeName));
            CanonicalizeRelayTreeOwner(bridgeRoot);
            AssertRelayTreeSecurity(bridgeRoot, canonicalSourceServiceSid);
            VerifyRelayBundle(relayBundleRoot, relayBundleInventory);

            controlPipe = CreateAuthenticatedPipe(
                controlPipeName,
                canonicalSourceServiceSid);
            using var manager = OpenRequiredServiceControlManager();
            ValidateSourceServiceAndProcessRunning(
                manager,
                canonicalSourceServiceName,
                sourceProcessHandle,
                sourceProcessId,
                sourceProcessCreatedAtUtcTicks);
            ValidateCapturedProcessImageAndHash(
                sourceProcessHandle,
                sourceProcessId,
                "source Station",
                fullSourceExecutablePath,
                canonicalSourceExecutableSha256);
            ValidateSourceProcessOutsideJob(sourceProcessHandle, sourceProcessId);

            var runnerSid = WindowsIdentity.GetCurrent().User
                            ?? throw new InvalidOperationException(
                                "The service-token relay runner has no Windows SID.");
            using (var createOnlySource = OpenExactSourceCreationProcess(
                       sourceProcessHandle,
                       sourceProcessId))
            {
                relay = WindowsSourceTokenRelayProcess.CreateSuspended(
                    request,
                    createOnlySource,
                    runnerSid);
            }

            relay.ValidateCreated(request);
            ValidateSourceServiceAndProcessRunning(
                manager,
                canonicalSourceServiceName,
                sourceProcessHandle,
                sourceProcessId,
                sourceProcessCreatedAtUtcTicks);
            ValidateCapturedProcessImageAndHash(
                sourceProcessHandle,
                sourceProcessId,
                "source Station",
                fullSourceExecutablePath,
                canonicalSourceExecutableSha256);
            ValidateSourceProcessOutsideJob(sourceProcessHandle, sourceProcessId);
            VerifyRelayBundle(relayBundleRoot, relayBundleInventory);
            AssertRelayTreeSecurity(bridgeRoot, canonicalSourceServiceSid);

            relay.Resume();
            WaitForRelayConnection(controlPipe, relay);
            relay.ValidateRunning(request);
            ValidateRelayControlClient(
                controlPipe,
                relay.ProcessId,
                canonicalSourceServiceSid);

            var receivedNonce = new byte[NonceBytes];
            ReadExactlyWithTimeout(
                controlPipe,
                receivedNonce,
                TransitionTimeout);
            if (!CryptographicOperations.FixedTimeEquals(
                    receivedNonce,
                    nonceBytes))
            {
                throw new InvalidDataException(
                    "The source-token relay control nonce does not match its request.");
            }

            ValidateSourceServiceAndProcessRunning(
                manager,
                canonicalSourceServiceName,
                sourceProcessHandle,
                sourceProcessId,
                sourceProcessCreatedAtUtcTicks);
            relay.ValidateRunning(request);
            controlPipe.RunAsClient(() =>
            {
                try
                {
                    ValidateImpersonatedSourceIdentity(canonicalSourceServiceSid);
                    actionResult = action();
                    actionCompleted = true;
                }
                catch (Exception exception)
                {
                    actionFailure = ExceptionDispatchInfo.Capture(exception);
                }
            });

            WriteReceipt(controlPipe);
            relay.WaitForSuccessfulExit(TransitionTimeout);
            ValidateSourceServiceAndProcessRunning(
                manager,
                canonicalSourceServiceName,
                sourceProcessHandle,
                sourceProcessId,
                sourceProcessCreatedAtUtcTicks);
            ValidateCapturedProcessImageAndHash(
                sourceProcessHandle,
                sourceProcessId,
                "source Station",
                fullSourceExecutablePath,
                canonicalSourceExecutableSha256);
            ValidateSourceProcessOutsideJob(sourceProcessHandle, sourceProcessId);
            VerifyRelayBundle(relayBundleRoot, relayBundleInventory);
            AssertRelayTreeSecurity(bridgeRoot, canonicalSourceServiceSid);
            actionFailure?.Throw();
            if (!actionCompleted)
            {
                throw new InvalidOperationException(
                    "The exact Station service-token action did not complete.");
            }
        }
        catch (Exception exception)
        {
            operationFailure = ExceptionDispatchInfo.Capture(exception);
        }

        if (relay is not null)
        {
            try
            {
                relay.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (controlPipe is not null)
        {
            try
            {
                controlPipe.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }
        if (bridgeRootOwned)
        {
            try
            {
                DeleteRelayRoot(bridgeRoot);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if ((operationFailure is not null || cleanupFailures.Count != 0)
            && actionCompleted)
        {
            try
            {
                switch (actionResult)
                {
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                    case IAsyncDisposable asyncDisposable:
                        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        break;
                }
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
            finally
            {
                actionCompleted = false;
                actionResult = default;
            }
        }

        if (operationFailure is not null)
        {
            if (cleanupFailures.Count == 0)
            {
                operationFailure.Throw();
            }

            cleanupFailures.Insert(0, operationFailure.SourceException);
            throw new AggregateException(
                "The Windows source-token relay failed and scoped cleanup also failed.",
                cleanupFailures);
        }

        if (cleanupFailures.Count != 0)
        {
            throw new AggregateException(
                "The Windows source-token relay action succeeded but scoped cleanup failed.",
                cleanupFailures);
        }

        return actionResult!;
    }

    private static SafeProcessHandle OpenExactSourceCreationProcess(
        SafeProcessHandle retainedSourceProcess,
        uint sourceProcessId)
    {
        var process = OpenProcess(
            ProcessCreateProcess,
            inheritHandle: false,
            sourceProcessId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            process.Dispose();
            throw new Win32Exception(
                error,
                $"Could not acquire the exact source Station PID {sourceProcessId} create-only relay capability; Win32 error {error}.");
        }

        if (!CompareObjectHandles(retainedSourceProcess, process))
        {
            var error = Marshal.GetLastPInvokeError();
            process.Dispose();
            throw new Win32Exception(
                error,
                $"The create-only source Station handle does not refer to retained PID {sourceProcessId}; Win32 error {error}.");
        }

        return process;
    }

    private static void PrepareRelayRoot(
        string relayRoot,
        string sourceServiceSid)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The service-token relay runner has no Windows SID.");
        var security = new DirectorySecurity();
        security.SetOwner(currentSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in AdministrativeSids(currentSid))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sourceServiceSid),
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        CreateProtectedDirectory(relayRoot, security);
    }

    private static IEnumerable<SecurityIdentifier> AdministrativeSids(
        SecurityIdentifier currentSid) =>
        new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            currentSid
        }.Distinct();

    private static void CreateProtectedDirectory(
        string path,
        DirectorySecurity security)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorBuffer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, descriptorBuffer, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = descriptorBuffer,
                InheritHandle = false
            };
            if (!CreateDirectory(path, ref attributes))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(
                    error,
                    error == ErrorAlreadyExists
                        ? $"Protected source-token relay root '{path}' already exists."
                        : $"Could not atomically create protected source-token relay root '{path}'.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorBuffer);
        }
    }

    internal static Dictionary<string, RelayBundleFile> CopyRelayBundle(
        string source,
        string destination)
    {
        var fullSource = Path.GetFullPath(source);
        if (!Directory.Exists(fullSource))
        {
            throw new DirectoryNotFoundException(
                $"The Windows service-token Test Relay bundle '{fullSource}' is missing.");
        }

        var sourceRoot = new DirectoryInfo(fullSource);
        RejectReparseOrDevice(sourceRoot, "Test Relay bundle root");
        Directory.CreateDirectory(destination);
        var inventory = new Dictionary<string, RelayBundleFile>(StringComparer.Ordinal);
        var pending = new Stack<(DirectoryInfo Source, string Destination)>();
        pending.Push((sourceRoot, destination));
        while (pending.TryPop(out var current))
        {
            foreach (var entry in current.Source.EnumerateFileSystemInfos(
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparseOrDevice(entry, "Test Relay bundle entry");
                var target = Path.Combine(current.Destination, entry.Name);
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    pending.Push(((DirectoryInfo)entry, target));
                    continue;
                }

                var sourceFile = (FileInfo)entry;
                File.Copy(sourceFile.FullName, target, overwrite: false);
                var sourceSha256 = Sha256File(sourceFile.FullName);
                if (!string.Equals(
                        sourceSha256,
                        Sha256File(target),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The Test Relay copy changed '{sourceFile.FullName}'.");
                }

                var relativePath = Path.GetRelativePath(destination, target)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!inventory.TryAdd(
                        relativePath,
                        new RelayBundleFile(sourceFile.Length, sourceSha256)))
                {
                    throw new InvalidDataException(
                        $"The Test Relay bundle duplicates relative path '{relativePath}'.");
                }
            }
        }

        if (inventory.Count == 0)
        {
            throw new InvalidDataException("The Test Relay bundle is empty.");
        }

        return inventory;
    }

    internal static void VerifyRelayBundle(
        string bundleRoot,
        IReadOnlyDictionary<string, RelayBundleFile> expectedInventory)
    {
        ArgumentNullException.ThrowIfNull(expectedInventory);
        var root = new DirectoryInfo(Path.GetFullPath(bundleRoot));
        RejectReparseOrDevice(root, "protected Test Relay bundle root");
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos(
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparseOrDevice(entry, "protected Test Relay bundle entry");
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((DirectoryInfo)entry);
                    continue;
                }

                var relativePath = Path.GetRelativePath(root.FullName, entry.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!expectedInventory.TryGetValue(relativePath, out var expected)
                    || !observed.Add(relativePath))
                {
                    throw new InvalidDataException(
                        $"The protected Test Relay bundle contains unexpected file '{relativePath}'.");
                }

                var file = (FileInfo)entry;
                if (file.Length != expected.Length
                    || !string.Equals(
                        Sha256File(file.FullName),
                        expected.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The protected Test Relay bundle file '{relativePath}' changed.");
                }
            }
        }

        var missing = expectedInventory.Keys
            .Where(path => !observed.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidDataException(
                "The protected Test Relay bundle is missing files: "
                + string.Join(", ", missing)
                + ".");
        }
    }

    private static void RejectReparseOrDevice(
        FileSystemInfo entry,
        string role)
    {
        if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                $"The {role} '{entry.FullName}' is not an ordinary file-system entry.");
        }
    }

    internal static void CanonicalizeRelayTreeOwner(string relayRoot)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The source-token relay runner has no Windows SID.");
        var pending = new Stack<FileSystemInfo>();
        pending.Push(new DirectoryInfo(relayRoot));
        while (pending.TryPop(out var entry))
        {
            RejectReparseOrDevice(entry, "source-token relay tree entry");
            if (entry is DirectoryInfo directory)
            {
                var security = FileSystemAclExtensions.GetAccessControl(
                    directory,
                    AccessControlSections.Owner);
                if (security.GetOwner(typeof(SecurityIdentifier))
                    is not SecurityIdentifier owner
                    || !owner.Equals(currentSid))
                {
                    security.SetOwner(currentSid);
                    FileSystemAclExtensions.SetAccessControl(directory, security);
                }

                foreach (var child in directory.EnumerateFileSystemInfos(
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    pending.Push(child);
                }
                continue;
            }

            var file = (FileInfo)entry;
            var fileSecurity = FileSystemAclExtensions.GetAccessControl(
                file,
                AccessControlSections.Owner);
            if (fileSecurity.GetOwner(typeof(SecurityIdentifier))
                is not SecurityIdentifier fileOwner
                || !fileOwner.Equals(currentSid))
            {
                fileSecurity.SetOwner(currentSid);
                FileSystemAclExtensions.SetAccessControl(file, fileSecurity);
            }
        }
    }

    private static void AssertRelayTreeSecurity(
        string relayRoot,
        string sourceServiceSid)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The source-token relay runner has no Windows SID.");
        var administrativeSids = AdministrativeSids(currentSid)
            .Select(static sid => sid.Value)
            .ToHashSet(StringComparer.Ordinal);
        var sourceSid = new SecurityIdentifier(sourceServiceSid).Value;
        var allowedSids = new HashSet<string>(administrativeSids, StringComparer.Ordinal)
        {
            sourceSid
        };
        var pending = new Stack<FileSystemInfo>();
        pending.Push(new DirectoryInfo(relayRoot));
        while (pending.TryPop(out var entry))
        {
            RejectReparseOrDevice(entry, "protected source-token relay entry");
            FileSystemSecurity security = entry is DirectoryInfo directory
                ? FileSystemAclExtensions.GetAccessControl(directory)
                : FileSystemAclExtensions.GetAccessControl((FileInfo)entry);
            if (string.Equals(
                    entry.FullName,
                    relayRoot,
                    StringComparison.OrdinalIgnoreCase)
                && !security.AreAccessRulesProtected)
            {
                throw new InvalidDataException(
                    "The source-token relay root must be protected from parent ACL inheritance.");
            }

            if (security.GetOwner(typeof(SecurityIdentifier))
                is not SecurityIdentifier owner
                || !owner.Equals(currentSid))
            {
                throw new InvalidDataException(
                    $"The protected source-token relay entry '{entry.FullName}' has an unexpected owner.");
            }

            var granted = allowedSids.ToDictionary(
                static sid => sid,
                static _ => (FileSystemRights)0,
                StringComparer.Ordinal);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                         includeExplicit: true,
                         includeInherited: true,
                         typeof(SecurityIdentifier)))
            {
                var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
                if (rule.AccessControlType != AccessControlType.Allow
                    || !granted.ContainsKey(sid))
                {
                    throw new InvalidDataException(
                        $"The protected source-token relay entry '{entry.FullName}' has an unexpected ACL rule.");
                }
                granted[sid] |= rule.FileSystemRights;
            }

            if (administrativeSids.Any(sid =>
                    (granted[sid] & FileSystemRights.FullControl)
                    != FileSystemRights.FullControl))
            {
                throw new InvalidDataException(
                    $"The protected source-token relay entry '{entry.FullName}' lacks required administrative ownership rights.");
            }

            var expectedSourceRights =
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
            if (granted[sourceSid] != expectedSourceRights)
            {
                throw new InvalidDataException(
                    $"The exact Station service SID has unexpected rights on '{entry.FullName}'.");
            }

            if (entry is DirectoryInfo childDirectory)
            {
                foreach (var child in childDirectory.EnumerateFileSystemInfos(
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static NamedPipeServerStream CreateAuthenticatedPipe(
        string pipeName,
        string clientSid)
    {
        var owner = WindowsIdentity.GetCurrent().User
                    ?? throw new InvalidOperationException(
                        "The source-token relay runner has no Windows SID.");
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            owner,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(clientSid),
            AuthenticatedPipeClientRights,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security,
            HandleInheritability.None);
    }

    private static void WaitForRelayConnection(
        NamedPipeServerStream pipe,
        WindowsSourceTokenRelayProcess relay)
    {
        using var deadline = new CancellationTokenSource(TransitionTimeout);
        var connection = pipe.WaitForConnectionAsync(deadline.Token);
        while (!connection.IsCompleted)
        {
            relay.EnsureRunning("before control-pipe connection");
            try
            {
                connection.Wait(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    "The source-token relay did not connect before its deadline.");
            }
        }
        connection.GetAwaiter().GetResult();
    }

    private static void ValidateRelayControlClient(
        NamedPipeServerStream pipe,
        uint expectedRelayProcessId,
        string expectedSourceServiceSid)
    {
        if (!GetNamedPipeClientProcessId(
                pipe.SafePipeHandle,
                out var actualProcessId))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not identify the source-token relay pipe client; Win32 error {error}.");
        }
        if (actualProcessId != expectedRelayProcessId)
        {
            throw new InvalidDataException(
                $"The source-token relay pipe client PID {actualProcessId} is not retained relay PID {expectedRelayProcessId}.");
        }

        Exception? identityFailure = null;
        pipe.RunAsClient(() =>
        {
            try
            {
                ValidateImpersonatedSourceIdentity(expectedSourceServiceSid);
            }
            catch (Exception exception)
            {
                identityFailure = exception;
            }
        });
        if (identityFailure is not null)
        {
            throw new InvalidOperationException(
                "The source-token relay pipe client did not prove the exact Station identity.",
                identityFailure);
        }
    }

    private static void ValidateImpersonatedSourceIdentity(
        string expectedSourceServiceSid)
    {
        var identity = WindowsStationServiceIdentityReader.ReadRequired(
            expectedSourceServiceSid);
        if (!string.Equals(
                identity.HostAccountSid,
                WindowsStationServiceIdentityReader.LocalServiceSid,
                StringComparison.Ordinal)
            || !string.Equals(
                identity.ServiceSid,
                expectedSourceServiceSid,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The control pipe did not impersonate the exact restricted LocalService Station token.");
        }
    }

    internal static void ValidateCapturedProcessImageAndHash(
        SafeProcessHandle process,
        uint processId,
        string role,
        string expectedExecutablePath,
        string expectedExecutableSha256)
    {
        var path = new char[32_768];
        var pathLength = checked((uint)path.Length);
        if (!QueryFullProcessImageName(
                process,
                flags: 0,
                path,
                ref pathLength)
            || pathLength == 0
            || pathLength >= path.Length)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not read {role} PID {processId} image path; Win32 error {error}.");
        }

        var actualPath = Path.GetFullPath(
            new string(path, 0, checked((int)pathLength)));
        if (!string.Equals(
                actualPath,
                Path.GetFullPath(expectedExecutablePath),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Sha256File(actualPath),
                expectedExecutableSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{role} PID {processId} does not run the exact expected image and hash.");
        }
    }

    private static SafeServiceHandle OpenRequiredServiceControlManager()
    {
        var manager = OpenSCManager(
            machineName: null,
            databaseName: null,
            ScManagerConnect);
        if (manager.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            manager.Dispose();
            throw new Win32Exception(
                error,
                $"Could not open the Service Control Manager for source-token relay validation; Win32 error {error}.");
        }
        return manager;
    }

    private static void ValidateSourceServiceAndProcessRunning(
        SafeServiceHandle manager,
        string sourceServiceName,
        SafeProcessHandle retainedSourceProcess,
        uint sourceProcessId,
        long expectedCreatedAtUtcTicks)
    {
        using var sourceService = OpenService(
            manager,
            sourceServiceName,
            ServiceQueryStatus);
        if (sourceService.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not open source Station service '{sourceServiceName}'; Win32 error {error}.");
        }

        var status = QueryStatus(sourceService, sourceServiceName);
        if (status.ServiceType != ServiceWin32OwnProcess
            || status.CurrentState != ServiceRunning
            || status.ProcessId != sourceProcessId)
        {
            throw new InvalidOperationException(
                $"Source Station service '{sourceServiceName}' is not Running as exact own-process PID {sourceProcessId}.");
        }

        ValidateExactProcessHandle(
            retainedSourceProcess,
            sourceProcessId,
            expectedCreatedAtUtcTicks,
            "retained source Station");
    }

    internal static void ValidateExactProcessHandle(
        SafeProcessHandle process,
        uint expectedProcessId,
        long expectedCreatedAtUtcTicks,
        string role)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.IsInvalid || process.IsClosed || expectedProcessId == 0)
        {
            throw new ArgumentException(
                $"A live {role} handle and positive PID are required.",
                nameof(process));
        }

        var actualProcessId = GetProcessId(process);
        if (actualProcessId != expectedProcessId)
        {
            throw new InvalidDataException(
                $"The {role} handle is PID {actualProcessId}, not {expectedProcessId}.");
        }

        var wait = WaitForSingleObject(process, milliseconds: 0);
        if (wait == WaitObject0)
        {
            throw new InvalidOperationException(
                $"The {role} PID {expectedProcessId} exited.");
        }
        if (wait == WaitFailed)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not inspect {role} PID {expectedProcessId}; Win32 error {error}.");
        }
        if (wait != WaitTimeout)
        {
            throw new InvalidOperationException(
                $"The {role} PID {expectedProcessId} returned unexpected wait status 0x{wait:x8}.");
        }
        if (ReadProcessCreationUtcTicks(process) != expectedCreatedAtUtcTicks)
        {
            throw new InvalidDataException(
                $"The {role} PID {expectedProcessId} creation time changed.");
        }
    }

    private static void ValidateSourceProcessOutsideJob(
        SafeProcessHandle sourceProcess,
        uint sourceProcessId)
    {
        if (!IsProcessInJob(sourceProcess, IntPtr.Zero, out var sourceIsInJob))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not inspect source Station PID {sourceProcessId} job membership; Win32 error {error}.");
        }
        if (sourceIsInJob)
        {
            throw new InvalidOperationException(
                $"Source Station PID {sourceProcessId} belongs to a job, so relay containment cannot be proven.");
        }
    }

    private static ServiceStatusProcess QueryStatus(
        SafeServiceHandle service,
        string serviceName)
    {
        var size = checked((uint)Marshal.SizeOf<ServiceStatusProcess>());
        if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                out var status,
                size,
                out var required)
            || required > size)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not query Station service '{serviceName}'; Win32 error {error}.");
        }
        return status;
    }

    private static void ReadExactlyWithTimeout(
        Stream stream,
        Memory<byte> buffer,
        TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            stream.ReadExactlyAsync(buffer, deadline.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "The source-token relay did not complete its bounded protocol frame.");
        }
    }

    private static void WriteReceipt(Stream stream)
    {
        using var deadline = new CancellationTokenSource(TransitionTimeout);
        try
        {
            stream.WriteAsync(
                    new ReadOnlyMemory<byte>([CompletionReceipt]),
                    deadline.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            stream.FlushAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "The source-token relay receipt could not be sent before its deadline.");
        }
    }

    private static long ReadProcessCreationUtcTicks(
        SafeProcessHandle processHandle)
    {
        if (!GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not read process creation time; Win32 error {error}.");
        }
        return DateTime.FromFileTimeUtc(creationTime.ToLong()).Ticks;
    }

    private static string RequireSha256(
        string value,
        string parameterName) =>
        value.Length == 64
        && value.All(static character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f')
            ? value
            : throw new ArgumentException(
                $"{parameterName} must be an exact lowercase SHA-256.",
                parameterName);

    private static string Sha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void WriteJsonAtomically<T>(
        string path,
        T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(
            temporary,
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
        File.Move(temporary, path, overwrite: false);
    }

    private static void DeleteRelayRoot(string relayRoot)
    {
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(relayRoot);
        }
        catch (Exception exception) when (exception is FileNotFoundException
                                           or DirectoryNotFoundException)
        {
            return;
        }

        var deadline = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                if ((rootAttributes & FileAttributes.Directory) != 0)
                {
                    DeleteDirectoryWithoutFollowingReparsePoints(relayRoot);
                }
                else
                {
                    if ((rootAttributes & FileAttributes.ReparsePoint) == 0)
                    {
                        File.SetAttributes(relayRoot, FileAttributes.Normal);
                    }
                    File.Delete(relayRoot);
                }
                return;
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException)
            {
                lastFailure = exception;
                Thread.Sleep(25);
            }
        }

        throw new IOException(
            $"Source-token relay root '{relayRoot}' could not be removed.",
            lastFailure);
    }

    private static void DeleteDirectoryWithoutFollowingReparsePoints(
        string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory.FullName, recursive: false);
            return;
        }

        foreach (var entry in directory.EnumerateFileSystemInfos(
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryWithoutFollowingReparsePoints(entry.FullName);
                continue;
            }
            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                File.SetAttributes(entry.FullName, FileAttributes.Normal);
            }
            File.Delete(entry.FullName);
        }
        File.SetAttributes(directory.FullName, FileAttributes.Normal);
        Directory.Delete(directory.FullName, recursive: false);
    }

    internal sealed record RelayBundleFile(long Length, string Sha256);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record RelayRequestDocument(
        string Nonce,
        uint SourceProcessId,
        long SourceProcessCreatedAtUtcTicks,
        string SourceExecutablePath,
        string SourceExecutableSha256,
        string ExpectedSourceServiceSid,
        string RelayBundleRoot,
        string RelayExecutablePath,
        string RelayExecutableSha256,
        string ControlPipeName);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public long ToLong() => ((long)HighDateTime << 32) | LowDateTime;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle manager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        uint infoLevel,
        out ServiceStatusProcess status,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompareObjectHandles(
        SafeProcessHandle firstObjectHandle,
        SafeProcessHandle secondObjectHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeProcessHandle process,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        SafeProcessHandle process,
        IntPtr job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(
        string path,
        ref SecurityAttributes securityAttributes);
}
