using System.Buffers.Binary;
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
    private const uint ProcessTerminate = 0x00000001;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint Synchronize = 0x00100000;
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
    private const int ErrorInvalidParameter = 87;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private const uint ForcedHelperTerminationExitCode = 70;
    private const byte CompletionReceipt = 0xA5;
    private const byte RelayCreationGrant = 0xC1;
    private const byte ObservedRelayMarker = 0xD0;
    private const byte RelayCaptureAcknowledgement = 0xA0;
    private const byte PreparedRelayMarker = 0xD1;
    private const byte RelayResumeAcknowledgement = 0xA1;
    private const int NonceBytes = 32;
    private const int ObservedRelayFrameBytes = 1 + sizeof(uint);
    private const int RelayCaptureAcknowledgementFrameBytes = 1 + sizeof(long);
    private const int MaximumResultBytes = 4096;
    internal const PipeAccessRights AuthenticatedPipeClientRights =
        PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(30);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
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
        var coordinationPipeName = "openlineops-token-bridge-coordination-" + nonce;
        var controlPipeName = "openlineops-token-bridge-control-" + nonce;
        var bridgeRoot = Path.Combine(
            fullParentRoot,
            ".service-token-bridge-" + nonce[..16]);
        var protocolRoot = Path.Combine(bridgeRoot, "protocol");
        var requestPath = Path.Combine(protocolRoot, "request.json");
        var resultRoot = Path.Combine(bridgeRoot, "result");
        var resultPath = Path.Combine(resultRoot, "result.json");
        var helperBundleSource = Path.Combine(
            AppContext.BaseDirectory,
            "windows-service-token-test-helper");
        var helperBundleRoot = Path.Combine(bridgeRoot, "helper");
        var helperExecutablePath = Path.Combine(
            helperBundleRoot,
            "OpenLineOps.WindowsServiceToken.TestHelper.exe");

        SafeServiceHandle? manager = null;
        SafeServiceHandle? service = null;
        NamedPipeServerStream? coordinationPipe = null;
        NamedPipeServerStream? controlPipe = null;
        SafeProcessHandle? helperProcessHandle = null;
        SafeProcessHandle? relayProcessHandle = null;
        ExceptionDispatchInfo? actionFailure = null;
        ExceptionDispatchInfo? operationFailure = null;
        Exception? bridgeDiagnostic = null;
        WindowsProcessAccessLease? sourceProcessAccessLease = null;
        T? actionResult = default;
        var actionCompleted = false;
        var bridgeResultConsumed = false;
        var bridgeRootOwned = false;
        var serviceOwned = false;
        var serviceMarkedForDeletion = false;
        var helperServiceStarted = false;
        var helperProcessTerminationProven = false;
        var relayProcessTerminationProven = false;
        var relayObservedReceived = false;
        var relayReadyReceived = false;
        var relayProcessIdentityConfirmed = false;
        var sourceProcessLeaseRestored = false;
        var relayResumeAcknowledged = false;
        var relayProcessId = 0u;
        var relayProcessCreatedAtUtcTicks = 0L;
        var helperExecutableSha256 = string.Empty;
        try
        {
            PrepareBridgeRoot(
                bridgeRoot,
                bridgeServiceSid,
                canonicalSourceServiceSid);
            bridgeRootOwned = true;
            Directory.CreateDirectory(protocolRoot);
            var helperBundleInventory = CopyHelperBundle(
                helperBundleSource,
                helperBundleRoot);
            PrepareResultRoot(resultRoot, bridgeServiceSid);
            if (!File.Exists(helperExecutablePath))
            {
                throw new FileNotFoundException(
                    "The staged Windows service-token test helper executable is missing.",
                    helperExecutablePath);
            }
            helperExecutableSha256 = Sha256File(helperExecutablePath);

            var sourceProcessCreatedAtUtcTicks = ReadProcessCreationUtcTicks(
                sourceProcessHandle);
            var runnerSid = WindowsIdentity.GetCurrent().User
                            ?? throw new InvalidOperationException(
                                "The service-token bridge runner has no Windows SID.");
            var request = new BridgeRequest(
                bridgeServiceName,
                nonce,
                canonicalSourceServiceName,
                sourceProcessId,
                sourceProcessCreatedAtUtcTicks,
                fullSourceExecutablePath,
                canonicalSourceExecutableSha256,
                canonicalSourceServiceSid,
                runnerSid.Value,
                helperBundleRoot,
                helperExecutablePath,
                helperExecutableSha256,
                coordinationPipeName,
                controlPipeName,
                resultPath);
            WriteJsonAtomically(requestPath, request);
            CanonicalizeBridgeTreeOwner(bridgeRoot);
            AssertBridgeTreeSecurity(
                bridgeRoot,
                bridgeServiceSid,
                canonicalSourceServiceSid,
                resultRoot);
            VerifyHelperBundle(helperBundleRoot, helperBundleInventory);
            var bridgeServiceIdentity = new SecurityIdentifier(bridgeServiceSid);
            coordinationPipe = CreateAuthenticatedPipe(
                coordinationPipeName,
                bridgeServiceSid);

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
                $@"NT SERVICE\{bridgeServiceName}",
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
                bridgeServiceName);

            if (!StartService(service, argumentCount: 0, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    error == ErrorServiceAlreadyRunning
                        ? $"Service-token bridge '{bridgeServiceName}' was already running."
                        : $"Could not start service-token bridge '{bridgeServiceName}'.");
            }
            helperServiceStarted = true;

            helperProcessHandle = CaptureHelperProcess(
                service,
                bridgeServiceName,
                resultPath);
            ValidateCapturedHelperProcess(
                helperProcessHandle,
                helperExecutablePath,
                helperExecutableSha256);
            VerifyHelperBundle(helperBundleRoot, helperBundleInventory);
            AssertBridgeTreeSecurity(
                bridgeRoot,
                bridgeServiceSid,
                canonicalSourceServiceSid,
                resultRoot);
            WaitForBridgeConnection(
                coordinationPipe,
                service,
                bridgeServiceName,
                resultPath);
            ValidateCoordinationClient(
                coordinationPipe,
                helperProcessHandle,
                bridgeServiceSid);
            var coordinationNonce = new byte[NonceBytes];
            ReadExactlyWithTimeout(
                coordinationPipe,
                coordinationNonce,
                TransitionTimeout);
            if (!CryptographicOperations.FixedTimeEquals(
                    coordinationNonce,
                    nonceBytes))
            {
                throw new InvalidDataException(
                    "The service-token bridge coordination nonce does not match its request.");
            }

            DeleteServiceRequired(service, bridgeServiceName);
            serviceMarkedForDeletion = true;

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
            sourceProcessAccessLease = WindowsProcessAccessLease.Prepare(
                sourceProcessHandle,
                sourceProcessId,
                sourceProcessCreatedAtUtcTicks,
                bridgeServiceIdentity);
            sourceProcessAccessLease.ApplyRequired();
            WriteProtocolByte(coordinationPipe, RelayCreationGrant);

            var observedRelayFrame = new byte[ObservedRelayFrameBytes];
            ReadExactlyWithTimeout(
                coordinationPipe,
                observedRelayFrame,
                TransitionTimeout);
            relayProcessId = ParseObservedRelayFrame(observedRelayFrame);
            relayProcessHandle = OpenObservedRelayProcess(relayProcessId);
            relayObservedReceived = true;
            relayProcessCreatedAtUtcTicks = ReadProcessCreationUtcTicks(
                relayProcessHandle);
            ValidateCapturedRelayProcess(
                relayProcessHandle,
                relayProcessId,
                relayProcessCreatedAtUtcTicks,
                helperExecutablePath,
                helperExecutableSha256);
            WriteRelayCaptureAcknowledgement(
                coordinationPipe,
                relayProcessCreatedAtUtcTicks);
            var preparedRelayMarker = new byte[1];
            ReadExactlyWithTimeout(
                coordinationPipe,
                preparedRelayMarker,
                TransitionTimeout);
            if (preparedRelayMarker[0] != PreparedRelayMarker)
            {
                throw new InvalidDataException(
                    "The helper did not send the exact validated prepared-relay marker.");
            }
            relayReadyReceived = true;
            relayProcessIdentityConfirmed = true;
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
            VerifyHelperBundle(helperBundleRoot, helperBundleInventory);
            AssertBridgeTreeSecurity(
                bridgeRoot,
                bridgeServiceSid,
                canonicalSourceServiceSid,
                resultRoot);

            sourceProcessAccessLease.Dispose();
            sourceProcessAccessLease = null;
            sourceProcessLeaseRestored = true;
            controlPipe = CreateAuthenticatedPipe(
                controlPipeName,
                canonicalSourceServiceSid);
            WriteProtocolByte(coordinationPipe, RelayResumeAcknowledgement);
            relayResumeAcknowledged = true;

            WaitForBridgeConnection(
                controlPipe,
                service,
                bridgeServiceName,
                resultPath);
            ValidateRelayControlClient(
                controlPipe,
                relayProcessHandle,
                relayProcessId,
                relayProcessCreatedAtUtcTicks,
                helperExecutablePath,
                helperExecutableSha256);
            var controlNonce = new byte[NonceBytes];
            ReadExactlyWithTimeout(controlPipe, controlNonce, TransitionTimeout);
            if (!CryptographicOperations.FixedTimeEquals(controlNonce, nonceBytes))
            {
                throw new InvalidDataException(
                    "The service-token bridge control nonce does not match its request.");
            }

            ValidateSourceServiceAndProcessRunning(
                manager,
                canonicalSourceServiceName,
                sourceProcessHandle,
                sourceProcessId,
                sourceProcessCreatedAtUtcTicks);

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
            WaitForProcessExit(
                helperProcessHandle,
                bridgeServiceName,
                TransitionTimeout);
            helperProcessTerminationProven = true;
            VerifyHelperBundle(helperBundleRoot, helperBundleInventory);
            AssertBridgeTreeSecurity(
                bridgeRoot,
                bridgeServiceSid,
                canonicalSourceServiceSid,
                resultRoot);
            var bridgeResult = ReadRequiredResult(resultPath, nonce, sourceProcessId);
            bridgeResultConsumed = true;
            if (!HasResultRelayBinding(
                    relayProcessId,
                    relayProcessCreatedAtUtcTicks,
                    bridgeResult.RelayProcessId,
                    bridgeResult.RelayProcessCreatedAtUtcTicks))
            {
                throw new InvalidDataException(
                    "The helper result did not bind the exact relay process captured from the control pipe.");
            }
            if (!bridgeResult.IsSuccess())
            {
                throw new InvalidOperationException(
                    "The service-token bridge did not prove every required validation and control-pipe fact. "
                    + JsonSerializer.Serialize(bridgeResult, JsonOptions));
            }
            WaitForProcessExit(
                relayProcessHandle,
                $"source-token relay PID {relayProcessId}",
                TransitionTimeout);
            relayProcessTerminationProven = true;

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
        CaptureCleanupFailure(cleanupFailures, () => coordinationPipe?.Dispose());
        if (service is not null)
        {
            if (serviceOwned && !service.IsInvalid)
            {
                CaptureCleanupFailure(
                    cleanupFailures,
                    () => StopServiceRequired(service, bridgeServiceName));
                if (helperProcessHandle is not null
                    && !helperProcessTerminationProven)
                {
                    try
                    {
                        EnsureHelperProcessTerminated(
                            helperProcessHandle,
                            bridgeServiceName);
                        helperProcessTerminationProven = true;
                    }
                    catch (Exception exception)
                    {
                        cleanupFailures.Add(exception);
                    }
                }
                if (operationFailure is not null
                    && !bridgeResultConsumed
                    && helperProcessTerminationProven
                    && File.Exists(resultPath))
                {
                    try
                    {
                        var result = ReadRequiredResult(
                            resultPath,
                            nonce,
                            sourceProcessId);
                        if (relayObservedReceived
                            && !HasFailureResultRelayBinding(
                                relayProcessId,
                                relayProcessCreatedAtUtcTicks,
                                relayReadyReceived,
                                result.RelayProcessId,
                                result.RelayProcessCreatedAtUtcTicks,
                                result.RelayProcessValidated))
                        {
                            throw new InvalidDataException(
                                "The helper failure result did not bind the exact relay process captured before the control protocol failed.");
                        }
                        if (relayObservedReceived
                            && HasResultRelayBinding(
                                relayProcessId,
                                relayProcessCreatedAtUtcTicks,
                                result.RelayProcessId,
                                result.RelayProcessCreatedAtUtcTicks))
                        {
                            relayProcessIdentityConfirmed = true;
                        }
                        if (!relayObservedReceived
                            && HasReportedRelayProcess(result))
                        {
                            relayProcessId = result.RelayProcessId;
                            relayProcessCreatedAtUtcTicks =
                                result.RelayProcessCreatedAtUtcTicks;
                            relayProcessHandle = CaptureReportedRelayForCleanup(
                                relayProcessId,
                                relayProcessCreatedAtUtcTicks,
                                helperExecutablePath,
                                helperExecutableSha256,
                                out relayProcessTerminationProven);
                            relayProcessIdentityConfirmed =
                                result.RelayProcessCreatedAtUtcTicks
                                >= DateTime.UnixEpoch.Ticks
                                && result.RelayProcessCreatedAtUtcTicks
                                <= DateTime.MaxValue.Ticks;
                        }
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

                if (!serviceMarkedForDeletion)
                {
                    CaptureCleanupFailure(
                        cleanupFailures,
                        () => DeleteServiceRequired(service, bridgeServiceName));
                }
            }

            CaptureCleanupFailure(cleanupFailures, service.Dispose);
            service = null;
        }

        if (helperProcessHandle is not null)
        {
            CaptureCleanupFailure(cleanupFailures, helperProcessHandle.Dispose);
            helperProcessHandle = null;
        }

        if (relayProcessHandle is not null)
        {
            if (!relayProcessTerminationProven
                && relayProcessIdentityConfirmed)
            {
                try
                {
                    EnsureHelperProcessTerminated(
                        relayProcessHandle,
                        $"source-token relay PID {relayProcessId}");
                    relayProcessTerminationProven = true;
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }
            else if (!relayProcessTerminationProven
                     && helperProcessTerminationProven)
            {
                relayProcessTerminationProven = true;
            }
            else if (!relayProcessTerminationProven)
            {
                cleanupFailures.Add(new InvalidOperationException(
                    $"The provisional source-token relay PID {relayProcessId} was never identity-bound and its helper-owned kill-on-close job could not be proven closed."));
            }

            CaptureCleanupFailure(cleanupFailures, relayProcessHandle.Dispose);
            relayProcessHandle = null;
        }

        if (sourceProcessAccessLease is not null
            && (!helperProcessTerminationProven
                || (relayProcessId != 0 && !relayProcessTerminationProven))
            && helperServiceStarted)
        {
            cleanupFailures.Add(new InvalidOperationException(
                "Exact termination of the helper/relay process tree was not proven before emergency source-process DACL restoration; any already-open relay-creation handle could not be treated as revoked."));
        }

        if (relayResumeAcknowledged && !sourceProcessLeaseRestored)
        {
            cleanupFailures.Add(new InvalidOperationException(
                "The source-token relay was resumed before exact source-process DACL restoration was proven."));
        }
        if (actionCompleted
            && (!relayReadyReceived
                || !sourceProcessLeaseRestored
                || !relayResumeAcknowledged))
        {
            cleanupFailures.Add(new InvalidOperationException(
                "The service-token action completed without the authenticated prepared-relay and pre-resume lease-restoration handshake."));
        }

        if (sourceProcessAccessLease is not null)
        {
            try
            {
                sourceProcessAccessLease.Dispose();
                sourceProcessAccessLease = null;
                sourceProcessLeaseRestored = true;
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
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

    private static void PrepareBridgeRoot(
        string bridgeRoot,
        string bridgeServiceSid,
        string sourceServiceSid)
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
                     currentSid
                 }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(bridgeServiceSid),
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sourceServiceSid),
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        CreateProtectedDirectory(bridgeRoot, security, "service-token bridge root");
    }

    private static void PrepareResultRoot(
        string resultRoot,
        string bridgeServiceSid)
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
                     currentSid
                 }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(bridgeServiceSid),
            FileSystemRights.Modify
            | FileSystemRights.ReadPermissions
            | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        CreateProtectedDirectory(resultRoot, security, "service-token result root");
    }

    private static void CreateProtectedDirectory(
        string path,
        DirectorySecurity security,
        string description)
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
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    error == ErrorAlreadyExists
                        ? $"Protected {description} '{path}' already exists."
                        : $"Could not atomically create protected {description} '{path}'.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorBuffer);
        }
    }

    internal static Dictionary<string, HelperBundleFile> CopyHelperBundle(
        string source,
        string destination)
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
        var inventory = new Dictionary<string, HelperBundleFile>(
            StringComparer.Ordinal);
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
                var sourceSha256 = Sha256File(sourceFile.FullName);
                var targetSha256 = Sha256File(target);
                if (!string.Equals(
                        sourceSha256,
                        targetSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The service-token test helper copy changed '{sourceFile.FullName}'.");
                }
                var relativePath = Path.GetRelativePath(destination, target)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!inventory.TryAdd(
                        relativePath,
                        new HelperBundleFile(
                            sourceFile.Length,
                            sourceSha256)))
                {
                    throw new InvalidDataException(
                        $"The service-token test helper bundle duplicates relative path '{relativePath}'.");
                }
            }
        }

        if (inventory.Count == 0)
        {
            throw new InvalidDataException(
                "The service-token test helper bundle is empty.");
        }

        return inventory;
    }

    internal static void VerifyHelperBundle(
        string bundleRoot,
        IReadOnlyDictionary<string, HelperBundleFile> expectedInventory)
    {
        ArgumentNullException.ThrowIfNull(expectedInventory);
        var root = new DirectoryInfo(Path.GetFullPath(bundleRoot));
        if ((root.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                $"The protected helper bundle root '{root.FullName}' is not an ordinary directory.");
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos(
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = entry.Attributes;
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException(
                        $"The protected helper bundle contains a reparse or device entry '{entry.FullName}'.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
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
                        $"The protected helper bundle contains unexpected file '{relativePath}'.");
                }
                var file = (FileInfo)entry;
                if (file.Length != expected.Length
                    || !string.Equals(
                        Sha256File(file.FullName),
                        expected.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The protected helper bundle file '{relativePath}' changed.");
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
                "The protected helper bundle is missing files: "
                + string.Join(", ", missing)
                + ".");
        }
    }

    private static void AssertBridgeTreeSecurity(
        string bridgeRoot,
        string bridgeServiceSid,
        string sourceServiceSid,
        string resultRoot)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The service-token bridge runner has no Windows SID.");
        var allowedSids = new HashSet<string>(StringComparer.Ordinal)
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            currentSid.Value,
            new SecurityIdentifier(bridgeServiceSid).Value,
            new SecurityIdentifier(sourceServiceSid).Value
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
            var isResultEntry = string.Equals(
                                    entry.FullName,
                                    resultRoot,
                                    StringComparison.OrdinalIgnoreCase)
                                || entry.FullName.StartsWith(
                                    resultRoot + Path.DirectorySeparatorChar,
                                    StringComparison.OrdinalIgnoreCase);
            if ((string.Equals(
                     entry.FullName,
                     bridgeRoot,
                     StringComparison.OrdinalIgnoreCase)
                 || string.Equals(
                     entry.FullName,
                     resultRoot,
                     StringComparison.OrdinalIgnoreCase))
                && !security.AreAccessRulesProtected)
            {
                throw new InvalidDataException(
                    $"The service-token bridge security boundary '{entry.FullName}' must be protected from parent inheritance.");
            }

            var owner = (SecurityIdentifier?)security.GetOwner(typeof(SecurityIdentifier));
            var canonicalBridgeServiceSid = new SecurityIdentifier(bridgeServiceSid);
            if (owner is null
                || (!owner.Equals(currentSid)
                    && !(isResultEntry && owner.Equals(canonicalBridgeServiceSid))))
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

            var canonicalBridgeSid = canonicalBridgeServiceSid.Value;
            var canonicalSourceServiceSid = new SecurityIdentifier(sourceServiceSid).Value;
            var administrativeSids = new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                currentSid.Value
            };
            if (administrativeSids.Any(sid =>
                    (grantedRights[sid] & FileSystemRights.FullControl)
                    != FileSystemRights.FullControl))
            {
                throw new InvalidDataException(
                    $"The protected service-token bridge entry does not grant every administrative principal full control: '{entry.FullName}'.");
            }

            var exactReadOnlyRights = FileSystemRights.ReadAndExecute
                                      | FileSystemRights.Synchronize;
            var bridgeRights = grantedRights[canonicalBridgeSid];
            var expectedBridgeRights = isResultEntry
                ? FileSystemRights.Modify
                  | FileSystemRights.ReadPermissions
                  | FileSystemRights.Synchronize
                : exactReadOnlyRights;
            if (bridgeRights != expectedBridgeRights)
            {
                throw new InvalidDataException(
                    $"The exact helper service SID has unexpected rights in the protected bridge tree: '{entry.FullName}'.");
            }

            var sourceRights = grantedRights[canonicalSourceServiceSid];
            var exactSourceRights = isResultEntry
                ? (FileSystemRights)0
                : exactReadOnlyRights;
            if (sourceRights != exactSourceRights)
            {
                throw new InvalidDataException(
                    $"The exact source Station SID must have only ReadAndExecute and required synchronization access in the protected bridge tree: '{entry.FullName}'.");
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

    private static NamedPipeServerStream CreateAuthenticatedPipe(
        string pipeName,
        string clientSid)
    {
        var owner = WindowsIdentity.GetCurrent().User
                    ?? throw new InvalidOperationException(
                        "The service-token bridge runner has no Windows SID.");
        var client = new SecurityIdentifier(clientSid);
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            owner,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            client,
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

    private static void ValidateCoordinationClient(
        NamedPipeServerStream pipe,
        SafeProcessHandle retainedHelperProcess,
        string expectedHelperSid)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pipeClientProcessId)
            || pipeClientProcessId == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not bind the coordination pipe to its helper client PID; Win32 error {error}.");
        }

        var retainedHelperProcessId = GetProcessId(retainedHelperProcess);
        if (retainedHelperProcessId == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not read the retained helper process identifier; Win32 error {error}.");
        }
        if (pipeClientProcessId != retainedHelperProcessId)
        {
            throw new InvalidDataException(
                $"The coordination pipe client PID {pipeClientProcessId} is not retained helper PID {retainedHelperProcessId}.");
        }
        if (WaitForSingleObject(retainedHelperProcess, milliseconds: 0) != WaitTimeout)
        {
            throw new InvalidOperationException(
                $"Retained helper PID {retainedHelperProcessId} exited before coordination identity validation.");
        }

        var helperSid = new SecurityIdentifier(expectedHelperSid);
        var administratorsSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            if (identity.User is not { } user
                || !user.Equals(helperSid)
                || identity.Groups?.Contains(administratorsSid) == true)
            {
                throw new InvalidOperationException(
                    "The coordination pipe did not impersonate the exact non-administrative one-shot virtual service account.");
            }
        });
    }

    private static uint ParseObservedRelayFrame(
        ReadOnlySpan<byte> frame)
    {
        if (frame.Length != ObservedRelayFrameBytes
            || frame[0] != ObservedRelayMarker)
        {
            throw new InvalidDataException(
                "The helper did not send the exact observed-relay frame.");
        }

        var processId = BinaryPrimitives.ReadUInt32LittleEndian(
            frame.Slice(1, sizeof(uint)));
        if (processId == 0)
        {
            throw new InvalidDataException(
                "The helper sent an invalid observed-relay PID.");
        }

        return processId;
    }

    private static SafeProcessHandle OpenObservedRelayProcess(uint processId)
    {
        var process = OpenProcess(
            ProcessTerminate | ProcessQueryLimitedInformation | Synchronize,
            inheritHandle: false,
            processId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            process.Dispose();
            throw new Win32Exception(
                error,
                $"Could not retain exact source-token relay PID {processId}; Win32 error {error}.");
        }

        return process;
    }

    private static SafeProcessHandle? CaptureReportedRelayForCleanup(
        uint processId,
        long expectedCreatedAtUtcTicks,
        string expectedExecutablePath,
        string expectedExecutableSha256,
        out bool terminationProven)
    {
        terminationProven = false;
        if (expectedCreatedAtUtcTicks == 0)
        {
            throw new InvalidOperationException(
                $"Helper-reported source-token relay PID {processId} has no runner-captured creation time, so cleanup must not reopen a live PID-only process.");
        }

        var process = OpenProcess(
            ProcessTerminate | ProcessQueryLimitedInformation | Synchronize,
            inheritHandle: false,
            processId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            process.Dispose();
            if (error == ErrorInvalidParameter)
            {
                terminationProven = true;
                return null;
            }

            throw new Win32Exception(
                error,
                $"Could not recover the helper-reported source-token relay PID {processId} for cleanup; Win32 error {error}.");
        }

        try
        {
            var actualProcessId = GetProcessId(process);
            if (actualProcessId == 0)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"Could not read helper-reported source-token relay PID {processId}; Win32 error {error}.");
            }
            var actualCreatedAtUtcTicks = ReadProcessCreationUtcTicks(process);
            if (actualProcessId != processId)
            {
                terminationProven = true;
                process.Dispose();
                return null;
            }
            if (actualCreatedAtUtcTicks != expectedCreatedAtUtcTicks)
            {
                terminationProven = true;
                process.Dispose();
                return null;
            }

            var wait = WaitForSingleObject(process, milliseconds: 0);
            if (wait == WaitObject0)
            {
                terminationProven = true;
                process.Dispose();
                return null;
            }
            if (wait != WaitTimeout)
            {
                throw wait == WaitFailed
                    ? new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not inspect helper-reported source-token relay PID {processId} during cleanup.")
                    : new InvalidOperationException(
                        $"Helper-reported source-token relay PID {processId} returned unexpected wait status 0x{wait:x8} during cleanup.");
            }

            ValidateCapturedProcessImageAndHash(
                process,
                processId,
                "helper-reported source-token relay",
                expectedExecutablePath,
                expectedExecutableSha256);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static void ValidateCapturedHelperProcess(
        SafeProcessHandle process,
        string expectedExecutablePath,
        string expectedExecutableSha256)
    {
        var processId = GetProcessId(process);
        if (processId == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not read the retained helper process identifier; Win32 error {error}.");
        }

        var wait = WaitForSingleObject(process, milliseconds: 0);
        if (wait == WaitObject0)
        {
            throw new InvalidOperationException(
                $"Retained helper PID {processId} exited before image validation.");
        }
        if (wait == WaitFailed)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not validate retained helper PID {processId} liveness; Win32 error {error}.");
        }
        if (wait != WaitTimeout)
        {
            throw new InvalidOperationException(
                $"Retained helper PID {processId} returned unexpected wait status 0x{wait:x8}.");
        }

        ValidateCapturedProcessImageAndHash(
            process,
            processId,
            "one-shot helper",
            expectedExecutablePath,
            expectedExecutableSha256);
    }

    private static void ValidateRelayControlClient(
        NamedPipeServerStream pipe,
        SafeProcessHandle retainedRelayProcess,
        uint preparedProcessId,
        long preparedCreatedAtUtcTicks,
        string expectedExecutablePath,
        string expectedExecutableSha256)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pipeClientProcessId)
            || pipeClientProcessId == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not bind the relay control pipe to its client PID; Win32 error {error}.");
        }

        ValidateCapturedRelayProcess(
            retainedRelayProcess,
            preparedProcessId,
            preparedCreatedAtUtcTicks,
            expectedExecutablePath,
            expectedExecutableSha256);
        var retainedProcessId = GetProcessId(retainedRelayProcess);
        var retainedCreatedAtUtcTicks = ReadProcessCreationUtcTicks(retainedRelayProcess);
        if (!HasCapturedRelayBinding(
                preparedProcessId,
                preparedCreatedAtUtcTicks,
                pipeClientProcessId,
                retainedProcessId,
                retainedCreatedAtUtcTicks))
        {
            throw new InvalidDataException(
                "The relay control pipe client is not the exact authenticated prepared relay process.");
        }
    }

    internal static bool HasCapturedRelayBinding(
        uint preparedProcessId,
        long preparedCreatedAtUtcTicks,
        uint pipeClientProcessId,
        uint retainedProcessId,
        long retainedCreatedAtUtcTicks) =>
        preparedProcessId != 0
        && preparedCreatedAtUtcTicks >= DateTime.UnixEpoch.Ticks
        && preparedCreatedAtUtcTicks <= DateTime.MaxValue.Ticks
        && pipeClientProcessId == preparedProcessId
        && retainedProcessId == preparedProcessId
        && retainedCreatedAtUtcTicks == preparedCreatedAtUtcTicks;

    internal static bool HasResultRelayBinding(
        uint capturedProcessId,
        long capturedCreatedAtUtcTicks,
        uint resultProcessId,
        long resultCreatedAtUtcTicks) =>
        capturedProcessId != 0
        && capturedCreatedAtUtcTicks >= DateTime.UnixEpoch.Ticks
        && capturedCreatedAtUtcTicks <= DateTime.MaxValue.Ticks
        && resultProcessId == capturedProcessId
        && resultCreatedAtUtcTicks == capturedCreatedAtUtcTicks;

    internal static bool HasFailureResultRelayBinding(
        uint capturedProcessId,
        long capturedCreatedAtUtcTicks,
        bool relayReadyReceived,
        uint resultProcessId,
        long resultCreatedAtUtcTicks,
        bool resultValidated) =>
        HasResultRelayBinding(
            capturedProcessId,
            capturedCreatedAtUtcTicks,
            resultProcessId,
            resultCreatedAtUtcTicks)
        || (!relayReadyReceived
            && capturedProcessId != 0
            && resultProcessId == capturedProcessId
            && !resultValidated
            && resultCreatedAtUtcTicks == 0
            && (capturedCreatedAtUtcTicks == 0
                || (capturedCreatedAtUtcTicks >= DateTime.UnixEpoch.Ticks
                    && capturedCreatedAtUtcTicks <= DateTime.MaxValue.Ticks)));

    private static void ValidateCapturedRelayProcess(
        SafeProcessHandle process,
        uint expectedProcessId,
        long expectedCreatedAtUtcTicks,
        string expectedExecutablePath,
        string expectedExecutableSha256)
    {
        var actualProcessId = GetProcessId(process);
        if (actualProcessId == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not read retained source-token relay PID; Win32 error {error}.");
        }
        var wait = WaitForSingleObject(process, milliseconds: 0);
        if (wait != WaitTimeout)
        {
            throw wait == WaitObject0
                ? new InvalidOperationException(
                    $"Source-token relay PID {expectedProcessId} exited before binding validation.")
                : new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not validate source-token relay PID {expectedProcessId} liveness.");
        }

        var actualCreatedAtUtcTicks = ReadProcessCreationUtcTicks(process);
        if (!HasCapturedRelayBinding(
                expectedProcessId,
                expectedCreatedAtUtcTicks,
                expectedProcessId,
                actualProcessId,
                actualCreatedAtUtcTicks))
        {
            throw new InvalidDataException(
                $"The retained source-token relay does not match prepared PID {expectedProcessId} and its creation time.");
        }

        ValidateCapturedProcessImageAndHash(
            process,
            expectedProcessId,
            "source-token relay",
            expectedExecutablePath,
            expectedExecutableSha256);
    }

    private static void ValidateCapturedProcessImageAndHash(
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
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not read {role} PID {processId} image path; Win32 error {error}.");
        }

        var actualPath = Path.GetFullPath(
            new string(path, 0, checked((int)pathLength)));
        if (!string.Equals(
                actualPath,
                expectedExecutablePath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Sha256File(actualPath),
                expectedExecutableSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{role} PID {processId} does not run the exact protected helper image and hash.");
        }
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
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not open source Station service '{sourceServiceName}' for pre-action validation; Win32 error {error}.");
        }

        var status = QueryStatus(sourceService, sourceServiceName);
        if (status.ServiceType != ServiceWin32OwnProcess
            || status.CurrentState != ServiceRunning
            || status.ProcessId != sourceProcessId)
        {
            throw new InvalidOperationException(
                $"Source Station service '{sourceServiceName}' is not Running as exact own-process PID {sourceProcessId} before the action.");
        }

        WindowsProcessAccessLease.ValidateExactProcessHandle(
            retainedSourceProcess,
            sourceProcessId,
            expectedCreatedAtUtcTicks,
            "pre-action source Station");
    }

    private static void ValidateSourceProcessOutsideJob(
        SafeProcessHandle sourceProcess,
        uint sourceProcessId)
    {
        if (!IsProcessInJob(sourceProcess, IntPtr.Zero, out var sourceIsInJob))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not determine whether exact source Station PID {sourceProcessId} belongs to a job; Win32 error {error}.");
        }

        if (sourceIsInJob)
        {
            throw new InvalidOperationException(
                $"Source Station PID {sourceProcessId} belongs to a job, so its inherited job contract cannot be proven safe for source-token relay creation.");
        }
    }

    private static void WriteProtocolByte(Stream stream, byte value)
    {
        stream.WriteByte(value);
        stream.Flush();
    }

    private static void WriteRelayCaptureAcknowledgement(
        Stream stream,
        long createdAtUtcTicks)
    {
        if (createdAtUtcTicks < DateTime.UnixEpoch.Ticks
            || createdAtUtcTicks > DateTime.MaxValue.Ticks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAtUtcTicks),
                "The runner-captured relay creation time is outside the valid UTC tick range.");
        }

        var frame = new byte[RelayCaptureAcknowledgementFrameBytes];
        frame[0] = RelayCaptureAcknowledgement;
        BinaryPrimitives.WriteInt64LittleEndian(
            frame.AsSpan(1, sizeof(long)),
            createdAtUtcTicks);
        using var deadline = new CancellationTokenSource(TransitionTimeout);
        try
        {
            stream.WriteAsync(frame, deadline.Token).AsTask().GetAwaiter().GetResult();
            stream.FlushAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "The service-token bridge could not send its relay-capture acknowledgement before the deadline.");
        }
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
                "The service-token bridge did not complete its bounded protocol frame before the deadline.");
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

        return ParseValidatedResult(
            ReadBoundedResultDocument(resultPath),
            expectedNonce,
            expectedProcessId);
    }

    internal static void ValidateResultDocument(
        byte[] document,
        string expectedNonce,
        uint expectedProcessId) =>
        _ = ParseValidatedResult(document, expectedNonce, expectedProcessId);

    private static BridgeResult ParseValidatedResult(
        byte[] document,
        string expectedNonce,
        uint expectedProcessId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = JsonSerializer.Deserialize<BridgeResult>(document, JsonOptions)
                     ?? throw new InvalidDataException(
                         "The service-token bridge result is null.");
        if (!string.Equals(result.Nonce, expectedNonce, StringComparison.Ordinal)
            || result.SourceProcessId != expectedProcessId)
        {
            throw new InvalidDataException(
                "The service-token bridge result does not match its request.");
        }
        if (!HasValidResultContract(result))
        {
            throw new InvalidDataException(
                "The service-token bridge result contains invalid bounded failure diagnostics.");
        }

        return result;
    }

    private static bool HasValidResultContract(BridgeResult result)
    {
        if (result.Win32Error < 0)
        {
            return false;
        }

        return result.FailurePhase switch
        {
            "helper-identity" =>
                result.FailureReason == "helper-identity"
                && !result.HelperIdentityValidated
                && !result.SourceServiceValidated
                && !result.SourceProcessValidated
                && HasNoRelayProcess(result)
                && !result.SourceTokenValidated
                && !result.ControlPipeConnected
                && !result.ReceiptReceived,
            "runner-coordination" =>
                result.FailureReason == "connect-and-grant"
                && result.HelperIdentityValidated
                && !result.SourceServiceValidated
                && !result.SourceProcessValidated
                && HasNoRelayProcess(result)
                && !result.SourceTokenValidated
                && !result.ControlPipeConnected
                && !result.ReceiptReceived,
            "source-service" =>
                result.FailureReason == "source-service"
                && result.HelperIdentityValidated
                && !result.SourceServiceValidated
                && !result.SourceProcessValidated
                && HasNoRelayProcess(result)
                && !result.SourceTokenValidated
                && !result.ControlPipeConnected
                && !result.ReceiptReceived,
            "source-process" =>
                result.FailureReason is "open-process"
                    or "process-validation"
                && result.HelperIdentityValidated
                && result.SourceServiceValidated
                && !result.SourceProcessValidated
                && HasNoRelayProcess(result)
                && !result.SourceTokenValidated
                && !result.ControlPipeConnected
                && !result.ReceiptReceived,
            "source-relay" =>
                result.FailureReason is "create-source-token-relay"
                    or "relay-creation-time"
                    or "relay-observation"
                    or "relay-validation"
                    or "source-service-before-relay-ready"
                    or "relay-ready"
                    or "source-service-before-relay-resume"
                    or "resume-source-token-relay"
                    or "source-token-relay-exit"
                && result.HelperIdentityValidated
                && result.SourceServiceValidated
                && result.SourceProcessValidated
                && (result.FailureReason switch
                {
                    "create-source-token-relay" => HasNoRelayProcess(result),
                    "relay-observation" or "relay-creation-time" =>
                        HasReportedWithoutCreationTime(result),
                    "relay-validation" =>
                        HasObservedUnvalidatedRelayProcess(result),
                    _ => HasValidatedRelayProcess(result)
                })
                && !result.SourceTokenValidated
                && !result.ControlPipeConnected
                && !result.ReceiptReceived,
            "post-receipt-source" =>
                result.FailureReason == "source-service-post-receipt"
                && HasEveryValidationFlag(result),
            "source-relay-cleanup" =>
                result.FailureReason == "terminate-job-and-wait"
                && result.HelperIdentityValidated
                && result.SourceServiceValidated
                && result.SourceProcessValidated
                && ((HasReportedUnvalidatedRelayProcess(result)
                     && !result.SourceTokenValidated
                     && !result.ControlPipeConnected
                     && !result.ReceiptReceived)
                    || (HasValidatedRelayProcess(result)
                        && ((!result.SourceTokenValidated
                             && !result.ControlPipeConnected
                             && !result.ReceiptReceived)
                            || (result.SourceTokenValidated
                                && result.ControlPipeConnected
                                && result.ReceiptReceived)))),
            "none" =>
                result.FailureReason == "none"
                && result.Win32Error == 0
                && HasEveryValidationFlag(result),
            _ => false
        };
    }

    private static bool HasNoRelayProcess(BridgeResult result) =>
        result.RelayProcessId == 0
        && result.RelayProcessCreatedAtUtcTicks == 0
        && !result.RelayProcessValidated;

    private static bool HasValidatedRelayProcess(BridgeResult result) =>
        result.RelayProcessId > 0
        && result.RelayProcessCreatedAtUtcTicks >= DateTime.UnixEpoch.Ticks
        && result.RelayProcessCreatedAtUtcTicks <= DateTime.MaxValue.Ticks
        && result.RelayProcessValidated;

    private static bool HasObservedRelayProcess(BridgeResult result) =>
        result.RelayProcessId > 0
        && result.RelayProcessCreatedAtUtcTicks >= DateTime.UnixEpoch.Ticks
        && result.RelayProcessCreatedAtUtcTicks <= DateTime.MaxValue.Ticks;

    private static bool HasReportedRelayProcess(BridgeResult result) =>
        result.RelayProcessId > 0
        && (result.RelayProcessCreatedAtUtcTicks == 0
            || (result.RelayProcessCreatedAtUtcTicks >= DateTime.UnixEpoch.Ticks
                && result.RelayProcessCreatedAtUtcTicks <= DateTime.MaxValue.Ticks));

    private static bool HasReportedWithoutCreationTime(BridgeResult result) =>
        result.RelayProcessId > 0
        && result.RelayProcessCreatedAtUtcTicks == 0
        && !result.RelayProcessValidated;

    private static bool HasObservedUnvalidatedRelayProcess(BridgeResult result) =>
        HasObservedRelayProcess(result)
        && !result.RelayProcessValidated;

    private static bool HasReportedUnvalidatedRelayProcess(BridgeResult result) =>
        HasReportedRelayProcess(result)
        && !result.RelayProcessValidated;

    private static bool HasEveryValidationFlag(BridgeResult result) =>
        result.HelperIdentityValidated
        && result.SourceServiceValidated
        && result.SourceProcessValidated
        && HasValidatedRelayProcess(result)
        && result.SourceTokenValidated
        && result.ControlPipeConnected
        && result.ReceiptReceived;

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

    private static SafeProcessHandle CaptureHelperProcess(
        SafeServiceHandle service,
        string serviceName,
        string resultPath)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TransitionTimeout)
        {
            var before = QueryStatus(service, serviceName);
            if (before.CurrentState == ServiceStopped)
            {
                throw new InvalidOperationException(
                    $"Service-token bridge '{serviceName}' stopped before its exact process could be captured. "
                    + ReadFailureResultForDiagnostic(resultPath));
            }
            if (before.CurrentState == ServiceStartPending)
            {
                Thread.Sleep(10);
                continue;
            }
            if (before.CurrentState != ServiceRunning
                || before.ServiceType != ServiceWin32OwnProcess)
            {
                throw new InvalidOperationException(
                    $"Service-token bridge '{serviceName}' entered state {before.CurrentState} with service type {before.ServiceType} before exact process capture.");
            }
            if (before.ProcessId == 0)
            {
                Thread.Sleep(10);
                continue;
            }

            var process = OpenProcess(
                ProcessTerminate | ProcessQueryLimitedInformation | Synchronize,
                inheritHandle: false,
                before.ProcessId);
            if (process.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                process.Dispose();
                var afterFailure = QueryStatus(service, serviceName);
                if (afterFailure.CurrentState == ServiceStopped)
                {
                    throw new InvalidOperationException(
                        $"Service-token bridge '{serviceName}' stopped while its exact process was being captured. "
                        + ReadFailureResultForDiagnostic(resultPath));
                }

                throw new Win32Exception(
                    error,
                    $"Could not open exact service-token bridge PID {before.ProcessId}; Win32 error {error}.");
            }

            try
            {
                var actualProcessId = GetProcessId(process);
                if (actualProcessId == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(
                        error,
                        $"Could not read the exact service-token bridge process identifier; Win32 error {error}.");
                }
                var after = QueryStatus(service, serviceName);
                if (actualProcessId != before.ProcessId
                    || after.ProcessId != before.ProcessId
                    || after.ServiceType != ServiceWin32OwnProcess
                    || after.CurrentState != ServiceRunning)
                {
                    throw new InvalidOperationException(
                        $"Service-token bridge '{serviceName}' changed process identity while PID {before.ProcessId} was captured.");
                }

                var wait = WaitForSingleObject(process, milliseconds: 0);
                if (wait == WaitObject0)
                {
                    throw new InvalidOperationException(
                        $"Exact service-token bridge PID {before.ProcessId} exited during capture.");
                }
                if (wait == WaitFailed)
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(
                        error,
                        $"Could not inspect exact service-token bridge PID {before.ProcessId}; Win32 error {error}.");
                }
                if (wait != WaitTimeout)
                {
                    throw new InvalidOperationException(
                        $"Exact service-token bridge PID {before.ProcessId} returned unexpected wait status 0x{wait:x8}.");
                }

                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        throw new TimeoutException(
            $"Service-token bridge '{serviceName}' did not expose an exact process before its deadline. "
            + ReadFailureResultForDiagnostic(resultPath));
    }

    internal static void EnsureHelperProcessTerminated(
        SafeProcessHandle helperProcess,
        string serviceName)
    {
        var gracefulWait = WaitForSingleObject(
            helperProcess,
            checked((uint)TimeSpan.FromSeconds(5).TotalMilliseconds));
        if (gracefulWait == WaitObject0)
        {
            return;
        }
        if (gracefulWait == WaitFailed)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not wait for exact service-token bridge process '{serviceName}'; Win32 error {error}.");
        }
        if (gracefulWait != WaitTimeout)
        {
            throw new InvalidOperationException(
                $"Exact service-token bridge process '{serviceName}' returned unexpected wait status 0x{gracefulWait:x8}.");
        }

        if (!TerminateProcess(helperProcess, ForcedHelperTerminationExitCode))
        {
            var error = Marshal.GetLastWin32Error();
            var postTerminationWait = WaitForSingleObject(
                helperProcess,
                milliseconds: 0);
            if (postTerminationWait == WaitObject0)
            {
                return;
            }
            if (postTerminationWait == WaitFailed)
            {
                var waitError = Marshal.GetLastWin32Error();
                throw new AggregateException(
                    $"Could not terminate or recheck exact stuck service-token bridge process '{serviceName}'.",
                    new Win32Exception(
                        error,
                        $"TerminateProcess failed with Win32 error {error}."),
                    new Win32Exception(
                        waitError,
                        $"WaitForSingleObject failed with Win32 error {waitError}."));
            }
            if (postTerminationWait != WaitTimeout)
            {
                throw new InvalidOperationException(
                    $"Exact service-token bridge process '{serviceName}' returned unexpected post-termination wait status 0x{postTerminationWait:x8}.");
            }

            throw new Win32Exception(
                error,
                $"Could not terminate exact stuck service-token bridge process '{serviceName}'; Win32 error {error}.");
        }

        WaitForProcessExit(
            helperProcess,
            serviceName,
            TransitionTimeout);
    }

    private static void WaitForProcessExit(
        SafeProcessHandle process,
        string serviceName,
        TimeSpan timeout)
    {
        var milliseconds = checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
        var wait = WaitForSingleObject(process, milliseconds);
        if (wait == WaitObject0)
        {
            return;
        }
        if (wait == WaitTimeout)
        {
            throw new TimeoutException(
                $"Exact service-token bridge process '{serviceName}' did not exit before its deadline.");
        }
        if (wait == WaitFailed)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Could not wait for exact service-token bridge process '{serviceName}'; Win32 error {error}.");
        }

        throw new InvalidOperationException(
            $"Exact service-token bridge process '{serviceName}' returned unexpected wait status 0x{wait:x8}.");
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
        string serviceName)
    {
        var currentSid = WindowsIdentity.GetCurrent().User
                         ?? throw new InvalidOperationException(
                             "The service-token bridge runner has no Windows SID.");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var dacl = new RawAcl(GenericAcl.AclRevision, capacity: 3);
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
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(bridgeRoot);
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
                    DeleteBridgeDirectoryWithoutFollowingReparsePoints(bridgeRoot);
                }
                else
                {
                    if ((rootAttributes & FileAttributes.ReparsePoint) == 0)
                    {
                        File.SetAttributes(bridgeRoot, FileAttributes.Normal);
                    }
                    File.Delete(bridgeRoot);
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

    internal sealed record HelperBundleFile(long Length, string Sha256);

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
        string RunnerSid,
        string HelperBundleRoot,
        string HelperExecutablePath,
        string HelperExecutableSha256,
        string CoordinationPipeName,
        string ControlPipeName,
        string ResultPath);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record BridgeResult(
        string Nonce,
        uint SourceProcessId,
        uint RelayProcessId,
        long RelayProcessCreatedAtUtcTicks,
        bool HelperIdentityValidated,
        bool SourceServiceValidated,
        bool SourceProcessValidated,
        bool RelayProcessValidated,
        bool SourceTokenValidated,
        bool ControlPipeConnected,
        bool ReceiptReceived,
        string FailurePhase,
        string FailureReason,
        int Win32Error)
    {
        public bool IsSuccess() => HasValidResultContract(this)
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        SafeProcessHandle process,
        IntPtr job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executablePath,
        ref uint executablePathLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeProcessHandle process,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(
        SafeProcessHandle process,
        uint exitCode);

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
