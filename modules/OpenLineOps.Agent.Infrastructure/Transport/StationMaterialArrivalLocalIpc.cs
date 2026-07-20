using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.ContentProtection;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed record StationMaterialArrivalLocalIpcOptions(
    string PipeName,
    string AuthorizedPrincipalSid,
    int MaximumRequestBytes = 64 * 1024,
    TimeSpan RequestFrameTimeout = default)
{
    public static StationMaterialArrivalLocalIpcOptions ForStationServiceSid(
        string stationServiceSid,
        int maximumRequestBytes = 64 * 1024,
        TimeSpan requestFrameTimeout = default) =>
        new(
            DerivePipeName(stationServiceSid),
            stationServiceSid,
            maximumRequestBytes,
            requestFrameTimeout);

    public static string DerivePipeName(string stationServiceSid)
    {
        var canonicalSid = WindowsStationServiceIdentityReader.RequireCanonicalServiceSid(
            stationServiceSid,
            nameof(stationServiceSid));
        return "openlineops-material-"
               + Convert.ToHexStringLower(
                   SHA256.HashData(Encoding.ASCII.GetBytes(canonicalSid)));
    }

    public TimeSpan ResolveRequestFrameTimeout()
    {
        if (RequestFrameTimeout == default)
        {
            return TimeSpan.FromSeconds(5);
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            RequestFrameTimeout,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            RequestFrameTimeout,
            TimeSpan.FromMinutes(1));
        return RequestFrameTimeout;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationMaterialArrivalLocalIpcResponse(
    Guid MessageId,
    bool Accepted,
    bool Replayed,
    string? FailureCode);

public sealed class StationMaterialArrivalLocalIpcServer(
    StationMaterialArrivalLocalIpcOptions options,
    StationMaterialArrivalReporter reporter)
{
    private const byte ResponseReceipt = 0xA5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    private readonly string _pipeName = RequirePipeName(options.PipeName);
    private readonly string _authorizedPrincipalSid =
        string.IsNullOrWhiteSpace(options.AuthorizedPrincipalSid)
            ? throw new ArgumentException(
                "Material arrival IPC authorized principal SID is required.",
                nameof(options))
            : options.AuthorizedPrincipalSid;
    private readonly int _maximumRequestBytes = options.MaximumRequestBytes is > 0 and <= 1024 * 1024
        ? options.MaximumRequestBytes
        : throw new ArgumentOutOfRangeException(
            nameof(options),
            "Material arrival IPC maximum request bytes must be within 1..1048576.");
    private readonly TimeSpan _requestFrameTimeout = options.ResolveRequestFrameTimeout();
    private readonly StationMaterialArrivalReporter _reporter =
        reporter ?? throw new ArgumentNullException(nameof(reporter));

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using var pipe = CreateServer();
        while (!cancellationToken.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ProcessConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The connected local caller exceeded the strict request-frame deadline.
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // The connected local caller disconnected before completing the protocol.
            }
            catch (InvalidDataException) when (!cancellationToken.IsCancellationRequested)
            {
                // The connected local caller sent an invalid response receipt.
            }
            finally
            {
                if (pipe.IsConnected)
                {
                    pipe.Disconnect();
                }
            }
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Material arrival IPC requires a Windows identity-bound named pipe.");
        }

        return WindowsIdentityBoundNamedPipe.CreateServer(
            _pipeName,
            _authorizedPrincipalSid,
            maximumServerInstances: 1,
            inputBufferSize: _maximumRequestBytes + sizeof(int),
            outputBufferSize: 4096);
    }

    private async Task ProcessConnectionAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        Guid messageId = Guid.Empty;
        StationMaterialArrivalLocalIpcResponse response;
        try
        {
            var payload = await ReadRequestFrameAsync(pipe, cancellationToken)
                .ConfigureAwait(false);
            var signal = JsonSerializer.Deserialize<StationMaterialArrivalSignal>(payload, JsonOptions)
                ?? throw new InvalidDataException("Material arrival IPC request is null.");
            messageId = signal.MessageId;
            var added = await _reporter.ReportAsync(signal, cancellationToken)
                .ConfigureAwait(false);
            response = new StationMaterialArrivalLocalIpcResponse(
                signal.MessageId,
                Accepted: true,
                Replayed: !added,
                FailureCode: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
                                           or InvalidDataException
                                           or ArgumentException
                                           or InvalidOperationException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            response = new StationMaterialArrivalLocalIpcResponse(
                messageId,
                Accepted: false,
                Replayed: false,
                FailureCode: Permanent(exception)
                    ? "Agent.MaterialArrivalSignalRejected"
                    : "Agent.MaterialArrivalSignalUnavailable");
        }

        try
        {
            await WriteFrameAsync(
                    pipe,
                    JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            await ReadResponseReceiptAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The local caller disconnected after submission; the durable outbox remains authoritative.
        }
    }

    internal async ValueTask ReadResponseReceiptAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestFrameTimeout);
        var receipt = new byte[1];
        await pipe.ReadExactlyAsync(receipt, deadline.Token).ConfigureAwait(false);
        if (receipt[0] != ResponseReceipt)
        {
            throw new InvalidDataException(
                "Material arrival IPC response receipt is invalid.");
        }
    }

    internal static async ValueTask WriteResponseReceiptAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new[] { ResponseReceipt }, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<byte[]> ReadRequestFrameAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestFrameTimeout);
        return await ReadFrameAsync(pipe, _maximumRequestBytes, deadline.Token)
            .ConfigureAwait(false);
    }

    internal static async ValueTask<byte[]> ReadFrameAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException(
                $"Material arrival IPC frame length must be within 1..{maximumBytes}.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    internal static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.IsEmpty)
        {
            throw new InvalidDataException("Material arrival IPC response is empty.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool Permanent(Exception exception) => exception is JsonException
        or InvalidDataException
        or ArgumentException;

    private static string RequirePipeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.Length > 128
            || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException(
                "Material arrival IPC pipe name must contain only ASCII letters, digits, dot, dash, or underscore.",
                nameof(value));
        }

        return value;
    }
}

public sealed class StationMaterialArrivalLocalIpcClient(
    StationMaterialArrivalLocalIpcOptions options)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    private readonly string _pipeName = string.IsNullOrWhiteSpace(options.PipeName)
        ? throw new ArgumentException("Material arrival IPC pipe name is required.", nameof(options))
        : options.PipeName;
    private readonly string _authorizedPrincipalSid =
        string.IsNullOrWhiteSpace(options.AuthorizedPrincipalSid)
            ? throw new ArgumentException(
                "Material arrival IPC authorized principal SID is required.",
                nameof(options))
            : options.AuthorizedPrincipalSid;
    private readonly int _maximumRequestBytes = options.MaximumRequestBytes;

    public async ValueTask<StationMaterialArrivalLocalIpcResponse> ReportAsync(
        StationMaterialArrivalSignal signal,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            connectTimeout,
            TimeSpan.Zero);

        var payload = JsonSerializer.SerializeToUtf8Bytes(signal, JsonOptions);
        if (payload.Length > _maximumRequestBytes)
        {
            throw new InvalidDataException(
                "Material arrival IPC request exceeds its configured maximum size.");
        }

        while (true)
        {
            try
            {
                return await ReportOnceAsync(
                        payload,
                        signal.MessageId,
                        connectTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A complete request may already be durable when its acknowledgement
                // connection drops. Reconnect under a fresh connection deadline and
                // submit the same idempotency key again.
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<StationMaterialArrivalLocalIpcResponse> ReportOnceAsync(
        ReadOnlyMemory<byte> payload,
        Guid expectedMessageId,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        await using var pipe = await ConnectPipeAsync(connectTimeout, cancellationToken)
            .ConfigureAwait(false);
        await StationMaterialArrivalLocalIpcServer
            .WriteFrameAsync(pipe, payload, cancellationToken)
            .ConfigureAwait(false);

        // Once the complete request frame has been handed to the Agent, package
        // verification, durable persistence, and acknowledgement are governed only
        // by the caller and Host lifetimes. A connection deadline must never cancel
        // an accepted hardware/material submission.
        var responsePayload = await StationMaterialArrivalLocalIpcServer
            .ReadFrameAsync(pipe, 4096, cancellationToken)
            .ConfigureAwait(false);
        StationMaterialArrivalLocalIpcResponse response;
        try
        {
            response = JsonSerializer.Deserialize<StationMaterialArrivalLocalIpcResponse>(
                           responsePayload,
                           JsonOptions)
                       ?? throw new InvalidDataException(
                           "Material arrival IPC response is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Material arrival IPC response violates the strict protocol schema.",
                exception);
        }

        ValidateResponse(response, expectedMessageId);
        await StationMaterialArrivalLocalIpcServer.WriteResponseReceiptAsync(
                pipe,
                cancellationToken)
            .ConfigureAwait(false);
        return response;
    }

    private static void ValidateResponse(
        StationMaterialArrivalLocalIpcResponse response,
        Guid expectedMessageId)
    {
        if (response.MessageId != expectedMessageId)
        {
            throw new InvalidDataException(
                "Material arrival IPC response does not match the submitted message.");
        }

        if (response.Accepted)
        {
            if (response.FailureCode is not null)
            {
                throw new InvalidDataException(
                    "Accepted material arrival IPC response cannot contain a failure code.");
            }

            return;
        }

        if (response.Replayed
            || response.FailureCode is not (
                "Agent.MaterialArrivalSignalRejected"
                or "Agent.MaterialArrivalSignalUnavailable"))
        {
            throw new InvalidDataException(
                "Rejected material arrival IPC response is inconsistent.");
        }
    }

    private async ValueTask<NamedPipeClientStream> ConnectPipeAsync(
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Material arrival IPC requires a Windows identity-bound named pipe.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(connectTimeout);
        while (true)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException(
                        "Material arrival IPC requires a Windows identity-bound named pipe.");
                }

                WindowsIdentityBoundNamedPipe.Verify(pipe, _authorizedPrincipalSid);
                return pipe;
            }
            catch (OperationCanceledException) when (
                deadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw new TimeoutException(
                    "Material arrival IPC did not connect before its connection deadline.");
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                try
                {
                    await Task.Delay(RetryDelay, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    deadline.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Material arrival IPC did not connect before its connection deadline.");
                }
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
