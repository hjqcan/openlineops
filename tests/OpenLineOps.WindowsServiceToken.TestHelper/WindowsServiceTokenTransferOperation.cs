using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.WindowsServiceToken.TestHelper;

[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceTokenTransferOperation(
    WindowsServiceTokenTransferRequest request)
{
    private const byte RunnerGrant = 0xC1;
    private const byte RelayObserved = 0xD0;
    private const byte RunnerCaptureAcknowledgement = 0xA0;
    private const byte RelayReady = 0xD1;
    private const byte RunnerReadyAcknowledgement = 0xA1;
    private const int RelayObservedBytes = 1 + sizeof(uint);
    private const int RunnerCaptureAcknowledgementBytes = 1 + sizeof(long);
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RelayCompletionTimeout = TimeSpan.FromSeconds(90);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var resultFile = new AtomicTokenTransferResult(request.ResultPath, request.Nonce);
        var failurePhase = "helper-identity";
        var failureReason = "helper-identity";
        var helperIdentityValidated = false;
        var sourceServiceValidated = false;
        var sourceProcessValidated = false;
        var relayProcessValidated = false;
        var sourceTokenValidated = false;
        var controlPipeConnected = false;
        var receiptReceived = false;
        var relayProcessId = 0u;
        var relayProcessCreatedAtUtcTicks = 0L;
        Exception? failureDiagnosticException = null;
        WindowsServiceTokenTransferResult? successResult = null;
        try
        {
            WindowsNative.ValidateHelperIdentity(request.HelperServiceName);
            helperIdentityValidated = true;

            failurePhase = "runner-coordination";
            failureReason = "connect-and-grant";
            await using var coordination = await ConnectAndAwaitGrantAsync(
                request,
                cancellationToken).ConfigureAwait(false);

            failurePhase = "source-service";
            failureReason = "source-service";
            using var sourceService = WindowsNative.OpenValidatedSourceService(
                request.SourceServiceName,
                request.SourceProcessId,
                request.ExpectedSourceServiceSid);
            sourceServiceValidated = true;

            failurePhase = "source-process";
            failureReason = "open-process";
            SourceTokenRelayProcess? relay = null;
            ExceptionDispatchInfo? relayOperationFailure = null;
            try
            {
                using (var creationSourceProcess = WindowsNative.OpenRequiredProcess(
                           request.SourceProcessId,
                           WindowsNative.ProcessCreateProcess
                           | WindowsNative.ProcessQueryLimitedInformation
                           | WindowsNative.Synchronize,
                           "source Station relay-creation"))
                {
                    failureReason = "process-validation";
                    ValidateSourceProcess(creationSourceProcess);
                    sourceProcessValidated = true;

                    failurePhase = "source-relay";
                    failureReason = "create-source-token-relay";
                    relay = SourceTokenRelayProcess.CreateSuspended(
                        request,
                        creationSourceProcess);
                    relayProcessId = relay.ProcessId;
                    failureReason = "relay-observation";
                    var runnerCapturedCreatedAtUtcTicks =
                        await SendObservedAndAwaitCaptureAsync(
                                coordination,
                                relay.ProcessId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    failureReason = "relay-creation-time";
                    relay.BindCreatedAtUtcTicks(runnerCapturedCreatedAtUtcTicks);
                    relayProcessCreatedAtUtcTicks = relay.CreatedAtUtcTicks;
                    failureReason = "relay-validation";
                    relay.ValidateCreated(request, creationSourceProcess);
                    relayProcessValidated = true;
                }

                failurePhase = "source-process";
                failureReason = "open-weak-process";
                using var weakSourceProcess = WindowsNative.OpenRequiredProcess(
                    request.SourceProcessId,
                    WindowsNative.ProcessQueryLimitedInformation
                    | WindowsNative.Synchronize,
                    "source Station weak validation");
                failureReason = "weak-process-validation";
                sourceService.EnsureRunning();
                ValidateSourceProcess(weakSourceProcess);

                failurePhase = "source-relay";
                failureReason = "relay-ready";
                await SendReadyAndAwaitResumeAsync(
                    coordination,
                    cancellationToken).ConfigureAwait(false);

                failureReason = "source-service-before-relay-resume";
                sourceService.EnsureRunning();
                failureReason = "source-process-before-relay-resume";
                ValidateSourceProcess(weakSourceProcess);
                failureReason = "resume-source-token-relay";
                relay.Resume();
                failureReason = "source-token-relay-exit";
                await relay.WaitForSuccessfulExitAsync(
                    RelayCompletionTimeout,
                    cancellationToken).ConfigureAwait(false);
                sourceTokenValidated = true;
                controlPipeConnected = true;
                receiptReceived = true;

                failurePhase = "post-receipt-source";
                failureReason = "source-service-post-receipt";
                sourceService.EnsureRunning();
                failureReason = "process-validation-post-receipt";
                ValidateSourceProcess(weakSourceProcess);
            }
            catch (Exception exception)
            {
                if (exception is SourceTokenRelayCreationException creationException)
                {
                    relayProcessId = creationException.ProcessId;
                    relayProcessCreatedAtUtcTicks = 0;
                    failurePhase = "source-relay-cleanup";
                    failureReason = "terminate-job-and-wait";
                    failureDiagnosticException = creationException.CleanupFailure;
                }
                relayOperationFailure = ExceptionDispatchInfo.Capture(exception);
            }

            Exception? relayCleanupFailure = null;
            if (relay is not null)
            {
                try
                {
                    relay.Dispose();
                }
                catch (Exception exception)
                {
                    relayCleanupFailure = exception;
                }
            }

            if (relayCleanupFailure is not null)
            {
                failurePhase = "source-relay-cleanup";
                failureReason = "terminate-job-and-wait";
                failureDiagnosticException = relayCleanupFailure;
            }
            if (relayOperationFailure is not null && relayCleanupFailure is not null)
            {
                throw new AggregateException(
                    "The source-token relay operation and its exact job/process cleanup both failed.",
                    relayOperationFailure.SourceException,
                    relayCleanupFailure);
            }
            if (relayCleanupFailure is not null)
            {
                throw relayCleanupFailure;
            }
            relayOperationFailure?.Throw();

            successResult = new WindowsServiceTokenTransferResult(
                request.Nonce,
                request.SourceProcessId,
                relayProcessId,
                relayProcessCreatedAtUtcTicks,
                helperIdentityValidated,
                sourceServiceValidated,
                sourceProcessValidated,
                relayProcessValidated,
                sourceTokenValidated,
                controlPipeConnected,
                receiptReceived,
                FailurePhase: "none",
                FailureReason: "none",
                Win32Error: 0);
        }
        catch (Exception operationFailure)
        {
            try
            {
                resultFile.Publish(new WindowsServiceTokenTransferResult(
                    request.Nonce,
                    request.SourceProcessId,
                    relayProcessId,
                    relayProcessCreatedAtUtcTicks,
                    helperIdentityValidated,
                    sourceServiceValidated,
                    sourceProcessValidated,
                    relayProcessValidated,
                    sourceTokenValidated,
                    controlPipeConnected,
                    receiptReceived,
                    failurePhase,
                    failureReason,
                    FindWin32Error(failureDiagnosticException ?? operationFailure)));
            }
            catch (Exception publicationFailure)
            {
                throw new AggregateException(
                    "The token-transfer operation and its bounded failure-result publication both failed.",
                    operationFailure,
                    publicationFailure);
            }

            throw;
        }

        resultFile.Publish(successResult
                           ?? throw new InvalidOperationException(
                               "The token-transfer operation produced no success result."));
    }

    private void ValidateSourceProcess(SafeProcessHandle sourceProcess)
    {
        WindowsNative.EnsureProcessAlive(
            sourceProcess,
            request.SourceProcessId,
            "source Station");
        WindowsNative.ValidateProcessCreationTime(
            sourceProcess,
            request.SourceProcessId,
            request.SourceProcessCreatedAtUtcTicks,
            "source Station");
        WindowsNative.ValidateProcessExecutablePath(
            sourceProcess,
            request.SourceProcessId,
            request.SourceExecutablePath,
            "source Station");
    }

    private static async Task<NamedPipeClientStream> ConnectAndAwaitGrantAsync(
        WindowsServiceTokenTransferRequest transferRequest,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            transferRequest.CoordinationPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await WithDeadlineAsync(
                token => pipe.ConnectAsync(token),
                "connect to the runner coordination pipe",
                cancellationToken).ConfigureAwait(false);
            var nonce = Convert.FromHexString(transferRequest.Nonce);
            await WithDeadlineAsync(
                async token =>
                {
                    await pipe.WriteAsync(nonce, token).ConfigureAwait(false);
                    await pipe.FlushAsync(token).ConfigureAwait(false);
                },
                "send the coordination nonce",
                cancellationToken).ConfigureAwait(false);
            var grant = new byte[1];
            await WithDeadlineAsync(
                token => pipe.ReadExactlyAsync(grant, token).AsTask(),
                "receive the runner creation grant",
                cancellationToken).ConfigureAwait(false);
            if (grant[0] != RunnerGrant)
            {
                throw new InvalidDataException(
                    "The runner coordination pipe did not return the exact one-byte 0xC1 creation grant.");
            }

            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long> SendObservedAndAwaitCaptureAsync(
        NamedPipeClientStream pipe,
        uint relayProcessId,
        CancellationToken cancellationToken)
    {
        if (relayProcessId == 0)
        {
            throw new InvalidDataException(
                "The suspended source-token relay has no valid process identifier for coordination.");
        }

        var observed = new byte[RelayObservedBytes];
        observed[0] = RelayObserved;
        BinaryPrimitives.WriteUInt32LittleEndian(
            observed.AsSpan(1, sizeof(uint)),
            relayProcessId);
        await WithDeadlineAsync(
            async token =>
            {
                await pipe.WriteAsync(observed, token).ConfigureAwait(false);
                await pipe.FlushAsync(token).ConfigureAwait(false);
            },
            "send the observed suspended relay identity",
            cancellationToken).ConfigureAwait(false);
        var acknowledgement = new byte[RunnerCaptureAcknowledgementBytes];
        await WithDeadlineAsync(
            token => pipe.ReadExactlyAsync(acknowledgement, token).AsTask(),
            "receive the runner relay-capture acknowledgement",
            cancellationToken).ConfigureAwait(false);
        if (acknowledgement[0] != RunnerCaptureAcknowledgement)
        {
            throw new InvalidDataException(
                "The runner coordination pipe did not return the exact 0xA0 relay-capture acknowledgement frame.");
        }
        var createdAtUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(
            acknowledgement.AsSpan(1, sizeof(long)));
        if (createdAtUtcTicks < DateTime.UnixEpoch.Ticks
            || createdAtUtcTicks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException(
                "The runner relay-capture acknowledgement contains an invalid creation time.");
        }

        return createdAtUtcTicks;
    }

    private static async Task SendReadyAndAwaitResumeAsync(
        NamedPipeClientStream pipe,
        CancellationToken cancellationToken)
    {
        await WithDeadlineAsync(
            async token =>
            {
                await pipe.WriteAsync(
                    new byte[] { RelayReady },
                    token).ConfigureAwait(false);
                await pipe.FlushAsync(token).ConfigureAwait(false);
            },
            "send the validated suspended relay marker",
            cancellationToken).ConfigureAwait(false);
        var acknowledgement = new byte[1];
        await WithDeadlineAsync(
            token => pipe.ReadExactlyAsync(acknowledgement, token).AsTask(),
            "receive the runner relay-resume acknowledgement",
            cancellationToken).ConfigureAwait(false);
        if (acknowledgement[0] != RunnerReadyAcknowledgement)
        {
            throw new InvalidDataException(
                "The runner coordination pipe did not return the exact one-byte 0xA1 relay-resume acknowledgement.");
        }
    }

    private static async Task WithDeadlineAsync(
        Func<CancellationToken, Task> operation,
        string description,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CoordinationTimeout);
        try
        {
            await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The token-transfer helper could not {description} before its bounded deadline.",
                exception);
        }
    }

    private static int FindWin32Error(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }
            if (current is Win32Exception win32Exception)
            {
                return win32Exception.NativeErrorCode;
            }
            if (current is AggregateException aggregateException)
            {
                foreach (var innerException in aggregateException.Flatten().InnerExceptions)
                {
                    pending.Push(innerException);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return 0;
    }
}
