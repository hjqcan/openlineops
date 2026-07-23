using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;

namespace OpenLineOps.WindowsServiceToken.TestRelay;

[SupportedOSPlatform("windows")]
internal static class SourceTokenRelayOperation
{
    private const byte AcceptedReceipt = 0xA5;
    private static readonly TimeSpan PipeConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> ExecuteAsync(string requestPath)
    {
        var request = RelayProtocol.ReadRequest(requestPath);
        if (!string.Equals(
                request.RequestPath,
                requestPath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The source-token relay request path changed during strict parsing.");
        }

        WindowsNative.ValidateCurrentSourceToken(request.ExpectedSourceServiceSid);
        ValidateCurrentRelayExecutable(request);
        ValidateSourceExecutableFile(request);

        var nonce = Convert.FromHexString(request.Nonce);
        await using var pipe = new NamedPipeClientStream(
            ".",
            request.ControlPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        using var connectionDeadline = new CancellationTokenSource(PipeConnectionTimeout);
        await pipe.ConnectAsync(connectionDeadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(nonce, connectionDeadline.Token).ConfigureAwait(false);
        await pipe.FlushAsync(connectionDeadline.Token).ConfigureAwait(false);

        using var receiptDeadline = new CancellationTokenSource(ReceiptTimeout);
        var receipt = new byte[1];
        var bytesRead = await pipe.ReadAsync(receipt, receiptDeadline.Token)
            .ConfigureAwait(false);
        if (bytesRead != 1 || receipt[0] != AcceptedReceipt)
        {
            throw new InvalidDataException(
                "The source-token relay control pipe did not return the exact one-byte 0xA5 receipt.");
        }

        WindowsNative.ValidateCurrentSourceToken(request.ExpectedSourceServiceSid);
        ValidateCurrentRelayExecutable(request);
        ValidateSourceExecutableFile(request);
        return 0;
    }

    private static void ValidateCurrentRelayExecutable(
        SourceTokenRelayRequest request)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)
            || !string.Equals(
                Path.GetFullPath(processPath),
                request.RelayExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The source-token relay is not executing the fixed image from its protected bundle.");
        }

        using var stream = new FileStream(
            request.RelayExecutablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.SequentialScan);
        WindowsNative.ValidateCanonicalExecutableHandle(
            stream.SafeFileHandle,
            request.RelayExecutablePath,
            "relay");
        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(
                actualSha256,
                request.RelayExecutableSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The source-token relay image hash differs from its protected request.");
        }
    }

    private static void ValidateSourceExecutableFile(
        SourceTokenRelayRequest request)
    {
        using var stream = new FileStream(
            request.SourceExecutablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.SequentialScan);
        WindowsNative.ValidateCanonicalExecutableHandle(
            stream.SafeFileHandle,
            request.SourceExecutablePath,
            "source Station");
        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(
                actualSha256,
                request.SourceExecutableSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The source Station executable hash differs from the strict relay request.");
        }
    }
}
