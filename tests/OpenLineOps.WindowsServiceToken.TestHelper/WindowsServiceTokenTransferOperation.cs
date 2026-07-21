using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;

namespace OpenLineOps.WindowsServiceToken.TestHelper;

[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceTokenTransferOperation(
    WindowsServiceTokenTransferRequest request)
{
    private static readonly TimeSpan PipeConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(60);
    private const byte AcceptedReceipt = 0xA5;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var resultFile = new AtomicTokenTransferResult(request.ResultPath, request.Nonce);
        var failurePhase = "helper-identity";
        var failureReason = "helper-identity";
        var helperIdentityValidated = false;
        var sourceServiceValidated = false;
        var sourceProcessValidated = false;
        var sourceTokenValidated = false;
        var controlPipeConnected = false;
        var receiptReceived = false;
        WindowsServiceTokenTransferResult? successResult = null;
        try
        {
            WindowsNative.ValidateHelperAndMarkForDeletion(request.HelperServiceName);
            helperIdentityValidated = true;

            failurePhase = "source-service";
            failureReason = "source-service";
            using var sourceService = WindowsNative.OpenValidatedSourceService(
                request.SourceServiceName,
                request.SourceProcessId,
                request.ExpectedSourceServiceSid);
            sourceServiceValidated = true;

            failurePhase = "source-process";
            failureReason = "open-process";
            using var sourceProcess = WindowsNative.OpenRequiredProcess(
                request.SourceProcessId,
                WindowsNative.ProcessQueryLimitedInformation | WindowsNative.Synchronize,
                "source Station");
            failureReason = "process-liveness";
            WindowsNative.EnsureProcessAlive(
                sourceProcess,
                request.SourceProcessId,
                "source Station");
            failureReason = "process-creation";
            WindowsNative.ValidateProcessCreationTime(
                sourceProcess,
                request.SourceProcessId,
                request.SourceProcessCreatedAtUtcTicks,
                "source Station");
            failureReason = "process-image";
            WindowsNative.ValidateProcessExecutablePath(
                sourceProcess,
                request.SourceProcessId,
                request.SourceExecutablePath,
                "source Station");
            sourceProcessValidated = true;

            failurePhase = "source-token";
            failureReason = "open-source-token";
            using var sourceToken = WindowsNative.OpenAndValidateSourceToken(
                sourceProcess,
                request.ExpectedSourceServiceSid);
            sourceTokenValidated = true;

            failurePhase = "source-executable";
            failureReason = "source-executable";
            WindowsIdentity.RunImpersonated(
                sourceToken,
                ValidateSourceExecutableFile);

            failurePhase = "post-validation-source";
            failureReason = "source-service-post-validation";
            sourceService.EnsureRunning();
            failureReason = "process-liveness-post-validation";
            WindowsNative.EnsureProcessAlive(
                sourceProcess,
                request.SourceProcessId,
                "source Station");
            failurePhase = "control-pipe";
            failureReason = "control-pipe";
            var nonce = Convert.FromHexString(request.Nonce);
            await using var pipe = ConnectAndSendNonceAsSourceToken(
                sourceToken,
                nonce);
            controlPipeConnected = true;

            failurePhase = "receipt";
            failureReason = "receipt";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReceiptTimeout);
            var receipt = new byte[1];
            var bytesRead = await pipe.ReadAsync(receipt, timeout.Token);
            if (bytesRead != 1 || receipt[0] != AcceptedReceipt)
            {
                throw new InvalidDataException(
                    "The control pipe did not return the exact one-byte 0xA5 receipt.");
            }

            receiptReceived = true;
            failurePhase = "post-receipt-source";
            failureReason = "source-service-post-receipt";
            sourceService.EnsureRunning();
            failureReason = "process-liveness-post-receipt";
            WindowsNative.EnsureProcessAlive(
                sourceProcess,
                request.SourceProcessId,
                "source Station");
            successResult = new WindowsServiceTokenTransferResult(
                request.Nonce,
                request.SourceProcessId,
                helperIdentityValidated,
                sourceServiceValidated,
                sourceProcessValidated,
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
                    helperIdentityValidated,
                    sourceServiceValidated,
                    sourceProcessValidated,
                    sourceTokenValidated,
                    controlPipeConnected,
                    receiptReceived,
                    failurePhase,
                    failureReason,
                    FindWin32Error(operationFailure)));
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

    private static int FindWin32Error(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception win32Exception)
            {
                return win32Exception.NativeErrorCode;
            }
        }

        return 0;
    }

    private NamedPipeClientStream ConnectAndSendNonceAsSourceToken(
        Microsoft.Win32.SafeHandles.SafeAccessTokenHandle sourceToken,
        byte[] nonce) =>
        WindowsIdentity.RunImpersonated(
            sourceToken,
            () =>
            {
                var pipe = new NamedPipeClientStream(
                    ".",
                    request.ControlPipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Impersonation);
                try
                {
                    pipe.Connect(checked((int)PipeConnectionTimeout.TotalMilliseconds));
                    pipe.Write(nonce);
                    pipe.Flush();
                    return pipe;
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
            });

    private void ValidateSourceExecutableFile()
    {
        using var stream = new FileStream(
            request.SourceExecutablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        WindowsNative.ValidateCanonicalSourceExecutableHandle(
            stream.SafeFileHandle,
            request.SourceExecutablePath);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(
                actual,
                request.SourceExecutableSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The source Station executable hash differs from the strict token-transfer request.");
        }
    }
}
