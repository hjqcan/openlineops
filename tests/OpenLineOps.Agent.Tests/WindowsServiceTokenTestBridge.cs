using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    private const uint ScManagerCreateService = 0x0002;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint ServiceAllAccess = 0x000F01FF;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceDemandStart = 0x00000003;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ScStatusProcessInfo = 0;
    private const uint ServiceConfigServiceSidInfo = 5;
    private const uint ServiceSidTypeUnrestricted = 1;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceMarkedForDelete = 1072;
    private const int ErrorAlreadyExists = 183;
    private const byte CompletionReceipt = 0xA5;
    private const int NonceBytes = 32;
    private const int MaximumResultBytes = 4096;

    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(30);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
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
        var derivedSourceServiceSid =
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                canonicalSourceServiceName);
        if (!string.Equals(
                derivedSourceServiceSid,
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
                $"The service-token bridge parent root '{fullParentRoot}' is missing.");
        }

        var fullSourceExecutablePath = Path.GetFullPath(sourceExecutablePath);
        if (!File.Exists(fullSourceExecutablePath))
        {
            throw new FileNotFoundException(
                "The source service executable is missing.",
                fullSourceExecutablePath);
        }

        var canonicalSourceExecutableSha256 = RequireSha256(
            sourceExecutableSha256,
            nameof(sourceExecutableSha256));
        var actualSourceExecutableSha256 = Sha256File(fullSourceExecutablePath);
        if (!string.Equals(
                canonicalSourceExecutableSha256,
                actualSourceExecutableSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The source service executable changed before service-token bridging.");
        }

        var nonceBytes = RandomNumberGenerator.GetBytes(NonceBytes);
        var nonce = Convert.ToHexStringLower(nonceBytes);
        var bridgeServiceName = "OpenLineOpsTokenBridge-" + nonce[..32];
        var bridgeServiceSid =
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                bridgeServiceName);
        var pipeName = "openlineops-token-bridge-" + nonce;
        var bridgeRoot = Path.Combine(
            fullParentRoot,
            ".service-token-bridge-" + nonce[..16]);
        var requestPath = Path.Combine(bridgeRoot, "request.json");
        var resultPath = Path.Combine(bridgeRoot, "result.json");
        var helperBundleSource = Path.Combine(
            AppContext.BaseDirectory,
            "windows-service-token-test-helper");
        var helperBundleRoot = Path.Combine(bridgeRoot, "helper");
        var helperExecutablePath = Path.Combine(
            helperBundleRoot,
            "OpenLineOps.WindowsServiceToken.TestHelper.exe");

        SafeServiceHandle? manager = null;
        SafeServiceHandle? service = null;
        NamedPipeServerStream? controlPipe = null;
        ExceptionDispatchInfo? actionFailure = null;
        ExceptionDispatchInfo? operationFailure = null;
        Exception? bridgeDiagnostic = null;
        T? actionResult = default;
        var actionCompleted = false;
        var bridgeResultConsumed = false;
        var bridgeRootOwned = false;
        var serviceOwned = false;
        try
        {
            PrepareBridgeRoot(bridgeRoot, bridgeServiceSid);
            bridgeRootOwned = true;
            AssertBridgeTreeSecurity(bridgeRoot, bridgeServiceSid);
            CopyHelperBundle(helperBundleSource, helperBundleRoot);
            if (!File.Exists(helperExecutablePath))
            {
                throw new FileNotFoundException(
                    "The staged Windows service-token test helper executable is missing.",
                    helperExecutablePath);
            }

            var request = new BridgeRequest(
                bridgeServiceName,
                nonce,
                canonicalSourceServiceName,
                sourceProcessId,
                ReadProcessCreationUtcTicks(sourceProcessHandle),
                fullSourceExecutablePath,
                canonicalSourceExecutableSha256,
                canonicalSourceServiceSid,
                pipeName,
                resultPath);
            WriteJsonAtomically(requestPath, request);
            CanonicalizeBridgeTreeOwner(bridgeRoot);
            AssertBridgeTreeSecurity(
                bridgeRoot,
                bridgeServiceSid);
            controlPipe = CreateControlPipe(pipeName, canonicalSourceServiceSid);

            manager = OpenSCManager(
                machineName: null,
                databaseName: null,
                ScManagerConnect | ScManagerCreateService);
            if (manager.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    "Could not open the Service Control Manager for the service-token test bridge.");
            }

            var binaryPath = QuoteServiceArgument(helperExecutablePath)
                             + " --request "
                             + QuoteServiceArgument(requestPath);
            service = CreateService(
                manager,
                bridgeServiceName,
                bridgeServiceName,
                DeleteAccess
                | WriteDac
                | ServiceChangeConfig
                | ServiceQueryStatus
                | ServiceStart
                | ServiceStop,
                ServiceWin32OwnProcess,
                ServiceDemandStart,
                ServiceErrorNormal,
                binaryPath,
                loadOrderGroup: null,
                IntPtr.Zero,
                dependencies: null,
                @"NT AUTHORITY\LocalService",
                password: null);
            if (service.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"Could not install one-shot service-token bridge '{bridgeServiceName}'.");
            }
            serviceOwned = true;

            ConfigureUnrestrictedServiceSid(
                service,
                bridgeServiceName);
            ProtectOneShotServiceObject(
                service,
                bridgeServiceName,
                bridgeServiceSid);

            if (!StartService(service, argumentCount: 0, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    error == ErrorServiceAlreadyRunning
                        ? $"Service-token bridge '{bridgeServiceName}' was already running."
                        : $"Could not start service-token bridge '{bridgeServiceName}'.");
            }

            WaitForBridgeConnection(controlPipe, service, bridgeServiceName, resultPath);
            var receivedNonce = new byte[NonceBytes];
            ReadExactlyWithTimeout(controlPipe, receivedNonce, TransitionTimeout);
            if (!CryptographicOperations.FixedTimeEquals(receivedNonce, nonceBytes))
            {
                throw new InvalidDataException(
                    "The service-token bridge control nonce does not match its request.");
            }

            controlPipe.RunAsClient(() =>
            {
                try
                {
                    var identity = WindowsStationServiceIdentityReader.ReadRequired(
                        canonicalSourceServiceSid);
                    if (!string.Equals(
                            identity.HostAccountSid,
                            WindowsStationServiceIdentityReader.LocalServiceSid,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            identity.ServiceSid,
                            canonicalSourceServiceSid,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The reverse control pipe did not impersonate the exact source Station service token.");
                    }

                    actionResult = action();
                    actionCompleted = true;
                }
                catch (Exception exception)
                {
                    actionFailure = ExceptionDispatchInfo.Capture(exception);
                }
            });

            WriteReceipt(controlPipe);
            WaitForStopped(service, bridgeServiceName, resultPath, TransitionTimeout);
            var bridgeResult = ReadRequiredResult(resultPath, nonce, sourceProcessId);
            bridgeResultConsumed = true;
            if (!bridgeResult.Success)
            {
                throw new InvalidOperationException(
                    "The service-token bridge did not prove every required validation and control-pipe fact. "
                    + JsonSerializer.Serialize(bridgeResult, JsonOptions));
            }

            actionFailure?.Throw();
            if (!actionCompleted)
            {
                throw new InvalidOperationException(
                    "The exact service-token action did not complete.");
            }

        }
        catch (Exception exception)
        {
            operationFailure = ExceptionDispatchInfo.Capture(exception);
        }

        var cleanupFailures = new List<Exception>();
        CaptureCleanupFailure(cleanupFailures, () => controlPipe?.Dispose());
        if (service is not null)
        {
            if (serviceOwned && !service.IsInvalid)
            {
                var serviceStopped = false;
                CaptureCleanupFailure(
                    cleanupFailures,
                    () =>
                    {
                        StopServiceRequired(service, bridgeServiceName);
                        serviceStopped = true;
                    });
                if (operationFailure is not null
                    && !bridgeResultConsumed
                    && serviceStopped
                    && File.Exists(resultPath))
                {
                    try
                    {
                        var result = ReadRequiredResult(
                            resultPath,
                            nonce,
                            sourceProcessId);
                        bridgeDiagnostic = new InvalidOperationException(
                            "The service-token bridge published this bound result after the control protocol failed: "
                            + JsonSerializer.Serialize(result, JsonOptions));
                    }
                    catch (Exception exception)
                    {
                        bridgeDiagnostic = new InvalidOperationException(
                            "The service-token bridge result published after the control protocol failed could not be validated.",
                            exception);
                    }
                }

                CaptureCleanupFailure(
                    cleanupFailures,
                    () => DeleteServiceRequired(service, bridgeServiceName));
            }

            CaptureCleanupFailure(cleanupFailures, service.Dispose);
            service = null;
        }

        if (manager is not null)
        {
            if (serviceOwned && !manager.IsInvalid)
            {
                CaptureCleanupFailure(
                    cleanupFailures,
                    () => WaitForDeletion(manager, bridgeServiceName, TransitionTimeout));
            }

            CaptureCleanupFailure(cleanupFailures, manager.Dispose);
        }

        if (bridgeRootOwned)
        {
            CaptureCleanupFailure(cleanupFailures, () => DeleteBridgeRoot(bridgeRoot));
        }

        if ((operationFailure is not null || cleanupFailures.Count > 0)
            && actionCompleted
            && actionResult is IDisposable disposableResult)
        {
            CaptureCleanupFailure(cleanupFailures, disposableResult.Dispose);
        }

        var hasIndependentActionFailure = actionFailure is not null
                                          && (operationFailure is null
                                              || !ReferenceEquals(
                                                  actionFailure.SourceException,
                                                  operationFailure.SourceException));
        if (operationFailure is not null
            && cleanupFailures.Count == 0
            && bridgeDiagnostic is null
            && !hasIndependentActionFailure)
        {
            operationFailure.Throw();
        }

        if (operationFailure is not null
            || bridgeDiagnostic is not null
            || hasIndependentActionFailure
            || cleanupFailures.Count > 0)
        {
            var failures = new List<Exception>();
            if (operationFailure is not null)
            {
                failures.Add(operationFailure.SourceException);
            }
            if (bridgeDiagnostic is not null)
            {
                failures.Add(bridgeDiagnostic);
            }
            if (hasIndependentActionFailure)
            {
                failures.Add(actionFailure!.SourceException);
            }

            failures.AddRange(cleanupFailures);
            throw new AggregateException(
                "The Windows service-token bridge failed and completed all scoped cleanup attempts.",
                failures);
        }

        return actionResult!;
    }

    private static void PrepareBridgeRoot(string bridgeRoot, string bridgeServiceSid)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The service-token bridge runner has no Windows SID.");
        var security = new DirectorySecurity();
        security.SetOwner(currentSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     currentSid,
                     new SecurityIdentifier(bridgeServiceSid)
                 }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

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
            if (!CreateDirectory(bridgeRoot, ref attributes))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    error == ErrorAlreadyExists
                        ? $"Service-token bridge root '{bridgeRoot}' already exists."
                        : $"Could not atomically create protected service-token bridge root '{bridgeRoot}'.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorBuffer);
        }
    }

    private static void CopyHelperBundle(string source, string destination)
    {
        var fullSource = Path.GetFullPath(source);
        if (!Directory.Exists(fullSource))
        {
            throw new DirectoryNotFoundException(
                $"The Windows service-token test helper bundle '{fullSource}' is missing.");
        }

        var sourceRoot = new DirectoryInfo(fullSource);
        if ((sourceRoot.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                $"The Windows service-token test helper bundle root '{fullSource}' is not an ordinary directory.");
        }

        Directory.CreateDirectory(destination);
        var pending = new Stack<(DirectoryInfo Source, string Destination)>();
        pending.Push((sourceRoot, destination));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in current.Source.EnumerateFileSystemInfos(
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = entry.Attributes;
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        $"The service-token test helper bundle contains a reparse or device entry '{entry.FullName}'.");
                }

                var target = Path.Combine(current.Destination, entry.Name);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    pending.Push(((DirectoryInfo)entry, target));
                    continue;
                }

                var sourceFile = (FileInfo)entry;
                File.Copy(sourceFile.FullName, target, overwrite: false);
                if (!string.Equals(
                        Sha256File(sourceFile.FullName),
                        Sha256File(target),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The service-token test helper copy changed '{sourceFile.FullName}'.");
                }
            }
        }
    }

    private static void AssertBridgeTreeSecurity(
        string bridgeRoot,
        string bridgeServiceSid)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The service-token bridge runner has no Windows SID.");
        var allowedSids = new HashSet<string>(StringComparer.Ordinal)
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            currentSid.Value,
            new SecurityIdentifier(bridgeServiceSid).Value
        };
        var pending = new Stack<FileSystemInfo>();
        pending.Push(new DirectoryInfo(bridgeRoot));
        while (pending.Count > 0)
        {
            var entry = pending.Pop();
            var attributes = entry.Attributes;
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(
                    $"The protected service-token bridge tree contains a reparse or device entry '{entry.FullName}'.");
            }

            FileSystemSecurity security = entry is DirectoryInfo directory
                ? FileSystemAclExtensions.GetAccessControl(directory)
                : FileSystemAclExtensions.GetAccessControl((FileInfo)entry);
            if (string.Equals(
                    entry.FullName,
                    bridgeRoot,
                    StringComparison.OrdinalIgnoreCase)
                && !security.AreAccessRulesProtected)
            {
                throw new InvalidDataException(
                    "The service-token bridge root DACL must be protected from parent inheritance.");
            }

            var owner = (SecurityIdentifier?)security.GetOwner(typeof(SecurityIdentifier));
            if (owner is null || !owner.Equals(currentSid))
            {
                throw new InvalidDataException(
                    $"The protected service-token bridge entry '{entry.FullName}' has unexpected owner "
                    + $"'{owner?.Value ?? "<missing>"}' instead of '{currentSid.Value}'.");
            }

            var grantedRights = allowedSids.ToDictionary(
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
                    || !grantedRights.ContainsKey(sid))
                {
                    throw new InvalidDataException(
                        $"The protected service-token bridge entry has an unexpected access rule: '{entry.FullName}'.");
                }

                grantedRights[sid] |= rule.FileSystemRights;
            }

            if (grantedRights.Any(pair =>
                    (pair.Value & FileSystemRights.FullControl) != FileSystemRights.FullControl))
            {
                throw new InvalidDataException(
                    $"The protected service-token bridge entry does not grant every exact principal full control: '{entry.FullName}'.");
            }

            if (entry is not DirectoryInfo childDirectory)
            {
                continue;
            }

            foreach (var child in childDirectory.EnumerateFileSystemInfos(
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                pending.Push(child);
            }
        }
    }

    internal static void CanonicalizeBridgeTreeOwner(string bridgeRoot)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The service-token bridge runner has no Windows SID.");
        var pending = new Stack<FileSystemInfo>();
        pending.Push(new DirectoryInfo(bridgeRoot));
        while (pending.Count > 0)
        {
            var entry = pending.Pop();
            var attributes = entry.Attributes;
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(
                    $"The service-token bridge tree contains a reparse or device entry '{entry.FullName}'.");
            }

            if (entry is DirectoryInfo directory)
            {
                var security = FileSystemAclExtensions.GetAccessControl(
                    directory,
                    AccessControlSections.Owner);
                var owner = (SecurityIdentifier?)security.GetOwner(
                    typeof(SecurityIdentifier));
                if (owner is null || !owner.Equals(currentSid))
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
            var fileOwner = (SecurityIdentifier?)fileSecurity.GetOwner(
                typeof(SecurityIdentifier));
            if (fileOwner is null || !fileOwner.Equals(currentSid))
            {
                fileSecurity.SetOwner(currentSid);
                FileSystemAclExtensions.SetAccessControl(file, fileSecurity);
            }
        }
    }

    private static NamedPipeServerStream CreateControlPipe(
        string pipeName,
        string sourceServiceSid)
    {
        var owner = WindowsIdentity.GetCurrent().User
                    ?? throw new InvalidOperationException(
                        "The service-token bridge runner has no Windows SID.");
        var sourceService = new SecurityIdentifier(sourceServiceSid);
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            sourceService,
            PipeAccessRights.FullControl,
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

    private static void WaitForBridgeConnection(
        NamedPipeServerStream pipe,
        SafeServiceHandle service,
        string serviceName,
        string resultPath)
    {
        using var deadline = new CancellationTokenSource(TransitionTimeout);
        var connection = pipe.WaitForConnectionAsync(deadline.Token);
        while (!connection.IsCompleted)
        {
            var status = QueryStatus(service, serviceName);
            if (status.CurrentState == ServiceStopped)
            {
                throw new InvalidOperationException(
                    $"Service-token bridge '{serviceName}' stopped before connecting. "
                    + ReadFailureResultForDiagnostic(resultPath));
            }

            try
            {
                connection.Wait(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Service-token bridge '{serviceName}' did not connect before its deadline. "
                    + ReadFailureResultForDiagnostic(resultPath));
            }
        }

        connection.GetAwaiter().GetResult();
    }

    private static void ReadExactlyWithTimeout(
        Stream stream,
        Memory<byte> buffer,
        TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            stream.ReadExactlyAsync(buffer, deadline.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "The service-token bridge did not send its complete nonce before the deadline.");
        }
    }

    private static void WriteReceipt(Stream stream)
    {
        stream.WriteByte(CompletionReceipt);
        stream.Flush();
    }

    private static BridgeResult ReadRequiredResult(
        string resultPath,
        string expectedNonce,
        uint expectedProcessId)
    {
        if (!File.Exists(resultPath))
        {
            throw new FileNotFoundException(
                "The service-token bridge did not write its required result.",
                resultPath);
        }

        var result = JsonSerializer.Deserialize<BridgeResult>(
                         ReadBoundedResultDocument(resultPath),
                         JsonOptions)
                     ?? throw new InvalidDataException(
                         "The service-token bridge result is null.");
        if (!string.Equals(result.Nonce, expectedNonce, StringComparison.Ordinal)
            || result.SourceProcessId != expectedProcessId)
        {
            throw new InvalidDataException(
                "The service-token bridge result does not match its request.");
        }

        return result;
    }

    private static string ReadFailureResultForDiagnostic(string resultPath)
    {
        try
        {
            return File.Exists(resultPath)
                ? "Result: " + StrictUtf8.GetString(
                    ReadBoundedResultDocument(resultPath))
                : "No result document was produced.";
        }
        catch (Exception exception)
        {
            return "The failure result could not be read: " + exception.Message;
        }
    }

    private static byte[] ReadBoundedResultDocument(string resultPath)
    {
        using var stream = new FileStream(
            resultPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (stream.Length is <= 0 or > MaximumResultBytes)
        {
            throw new InvalidDataException(
                $"The service-token bridge result length must be between 1 and {MaximumResultBytes} bytes.");
        }

        var document = new byte[checked((int)stream.Length)];
        stream.ReadExactly(document);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                "The service-token bridge result changed while it was read.");
        }

        return document;
    }

    private static long ReadProcessCreationUtcTicks(SafeProcessHandle processHandle)
    {
        if (!GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read source service process creation time.");
        }

        return DateTime.FromFileTimeUtc(creationTime.ToLong()).Ticks;
    }

    private static string RequireSha256(string value, string parameterName) =>
        value.Length == 64
        && value.All(static character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f')
            ? value
            : throw new ArgumentException(
                $"{parameterName} must be an exact lowercase SHA-256.",
                parameterName);

    private static string Sha256File(string path) => Convert.ToHexStringLower(
        SHA256.HashData(File.ReadAllBytes(path)));

    private static string QuoteServiceArgument(string value)
    {
        var fullPath = Path.GetFullPath(value);
        if (fullPath.Contains('"', StringComparison.Ordinal)
            || fullPath.EndsWith('\\'))
        {
            throw new InvalidDataException(
                $"Service-token bridge path '{fullPath}' cannot be safely quoted.");
        }

        return '"' + fullPath + '"';
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
        File.Move(temporary, path, overwrite: false);
    }

    private static ServiceStatusProcess QueryStatus(
        SafeServiceHandle service,
        string serviceName)
    {
        var status = new ServiceStatusProcess();
        if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                ref status,
                checked((uint)Marshal.SizeOf<ServiceStatusProcess>()),
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not query service-token bridge '{serviceName}'.");
        }

        return status;
    }

    private static void ConfigureUnrestrictedServiceSid(
        SafeServiceHandle service,
        string serviceName)
    {
        var info = new ServiceSidInfo
        {
            ServiceSidType = ServiceSidTypeUnrestricted
        };
        if (!ChangeServiceConfig2(
                service,
                ServiceConfigServiceSidInfo,
                ref info))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not configure unrestricted identity SID for service-token bridge '{serviceName}'.");
        }
    }

    private static void ProtectOneShotServiceObject(
        SafeServiceHandle service,
        string serviceName,
        string bridgeServiceSid)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The service-token bridge runner has no Windows SID.");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var helperSid = new SecurityIdentifier(bridgeServiceSid);
        var dacl = new RawAcl(GenericAcl.AclRevision, capacity: 4);
        foreach (var sid in new[] { systemSid, administratorsSid, currentSid }.Distinct())
        {
            dacl.InsertAce(dacl.Count, new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                checked((int)ServiceAllAccess),
                sid,
                isCallback: false,
                opaque: null));
        }

        dacl.InsertAce(dacl.Count, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            checked((int)(DeleteAccess | ServiceQueryConfig | ServiceQueryStatus)),
            helperSid,
            isCallback: false,
            opaque: null));
        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent
            | ControlFlags.DiscretionaryAclProtected,
            currentSid,
            administratorsSid,
            systemAcl: null,
            dacl);
        var binaryDescriptor = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binaryDescriptor, offset: 0);
        if (!SetServiceObjectSecurity(
                service,
                DaclSecurityInformation,
                binaryDescriptor))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not protect one-shot service-token bridge '{serviceName}' for its exact service SID.");
        }
    }

    private static void WaitForStopped(
        SafeServiceHandle service,
        string serviceName,
        string resultPath,
        TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            var status = QueryStatus(service, serviceName);
            if (status.CurrentState == ServiceStopped)
            {
                if (status.Win32ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Service-token bridge '{serviceName}' stopped with Win32 exit code {status.Win32ExitCode}. "
                        + ReadFailureResultForDiagnostic(resultPath));
                }

                return;
            }

            if (status.CurrentState is not (
                ServiceRunning or ServiceStartPending or ServiceStopPending))
            {
                throw new InvalidOperationException(
                    $"Service-token bridge '{serviceName}' entered unexpected state {status.CurrentState}. "
                    + ReadFailureResultForDiagnostic(resultPath));
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            $"Service-token bridge '{serviceName}' did not stop before its deadline. "
            + ReadFailureResultForDiagnostic(resultPath));
    }

    private static void CaptureCleanupFailure(
        List<Exception> failures,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void StopServiceRequired(
        SafeServiceHandle service,
        string serviceName)
    {
        var stopRequested = false;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TransitionTimeout)
        {
            var status = QueryStatus(service, serviceName);
            if (status.CurrentState == ServiceStopped)
            {
                return;
            }

            if (status.CurrentState == ServiceRunning && !stopRequested)
            {
                if (!ControlService(service, ServiceControlStop, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceNotActive)
                    {
                        throw new Win32Exception(
                            error,
                            $"Could not stop one-shot service-token bridge '{serviceName}'.");
                    }
                }

                stopRequested = true;
            }
            else if (status.CurrentState is not (
                         ServiceStartPending or ServiceRunning or ServiceStopPending))
            {
                throw new InvalidOperationException(
                    $"Service-token bridge '{serviceName}' entered unexpected cleanup state {status.CurrentState}.");
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            $"Service-token bridge '{serviceName}' did not stop during scoped cleanup.");
    }

    private static void DeleteServiceRequired(
        SafeServiceHandle service,
        string serviceName)
    {
        if (DeleteService(service))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error is ErrorServiceMarkedForDelete or ErrorServiceDoesNotExist)
        {
            return;
        }

        throw new Win32Exception(
            error,
            $"Could not mark one-shot service-token bridge '{serviceName}' for deletion.");
    }

    private static void WaitForDeletion(
        SafeServiceHandle manager,
        string serviceName,
        TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            using var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    return;
                }

                if (error == ErrorServiceMarkedForDelete)
                {
                    Thread.Sleep(25);
                    continue;
                }

                throw new Win32Exception(
                    error,
                    $"Could not verify deletion of service-token bridge '{serviceName}'.");
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            $"Service-token bridge '{serviceName}' was not deleted before its deadline.");
    }

    private static void DeleteBridgeRoot(string bridgeRoot)
    {
        if (!Directory.Exists(bridgeRoot))
        {
            return;
        }

        var deadline = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                DeleteBridgeDirectoryWithoutFollowingReparsePoints(bridgeRoot);
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
            $"Service-token bridge root '{bridgeRoot}' could not be removed.",
            lastFailure);
    }

    private static void DeleteBridgeDirectoryWithoutFollowingReparsePoints(
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
                DeleteBridgeDirectoryWithoutFollowingReparsePoints(entry.FullName);
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

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record BridgeRequest(
        string HelperServiceName,
        string Nonce,
        string SourceServiceName,
        uint SourceProcessId,
        long SourceProcessCreatedAtUtcTicks,
        string SourceExecutablePath,
        string SourceExecutableSha256,
        string ExpectedSourceServiceSid,
        string ControlPipeName,
        string ResultPath);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record BridgeResult(
        string Nonce,
        uint SourceProcessId,
        bool HelperIdentityValidated,
        bool SourceServiceValidated,
        bool SourceProcessValidated,
        bool SourceTokenValidated,
        bool ControlPipeConnected,
        bool ReceiptReceived,
        string FailurePhase)
    {
        public bool Success => HelperIdentityValidated
                               && SourceServiceValidated
                               && SourceProcessValidated
                               && SourceTokenValidated
                               && ControlPipeConnected
                               && ReceiptReceived
                               && string.Equals(
                                   FailurePhase,
                                   "none",
                                   StringComparison.Ordinal);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public long ToLong() => ((long)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
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
    private struct ServiceSidInfo
    {
        public uint ServiceSidType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
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
    private static extern SafeServiceHandle CreateService(
        SafeServiceHandle serviceControlManager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle serviceControlManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(
        SafeServiceHandle service,
        uint infoLevel,
        ref ServiceSidInfo info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceObjectSecurity(
        SafeServiceHandle service,
        uint securityInformation,
        byte[] securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(
        SafeServiceHandle service,
        uint argumentCount,
        IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        SafeServiceHandle service,
        uint control,
        out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        uint infoLevel,
        ref ServiceStatusProcess serviceStatus,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(SafeServiceHandle service);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(
        string path,
        ref SecurityAttributes securityAttributes);
}
