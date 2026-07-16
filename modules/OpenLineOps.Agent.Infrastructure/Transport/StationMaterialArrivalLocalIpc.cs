using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Application.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed record StationMaterialArrivalLocalIpcOptions(
    string PipeName,
    int MaximumRequestBytes = 64 * 1024,
    TimeSpan ConnectionTimeout = default)
{
    public TimeSpan ResolveConnectionTimeout()
    {
        if (ConnectionTimeout == default)
        {
            return TimeSpan.FromSeconds(5);
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            ConnectionTimeout,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            ConnectionTimeout,
            TimeSpan.FromMinutes(1));
        return ConnectionTimeout;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _pipeName = RequirePipeName(options.PipeName);
    private readonly int _maximumRequestBytes = options.MaximumRequestBytes is > 0 and <= 1024 * 1024
        ? options.MaximumRequestBytes
        : throw new ArgumentOutOfRangeException(
            nameof(options),
            "Material arrival IPC maximum request bytes must be within 1..1048576.");
    private readonly TimeSpan _connectionTimeout = options.ResolveConnectionTimeout();
    private readonly StationMaterialArrivalReporter _reporter =
        reporter ?? throw new ArgumentNullException(nameof(reporter));

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = CreateServer();
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ProcessConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The connected local caller exceeded the strict frame-processing deadline.
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // The connected local caller disconnected before completing the protocol.
            }
        }
    }

    private NamedPipeServerStream CreateServer() => new(
        _pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        inBufferSize: _maximumRequestBytes + sizeof(int),
        outBufferSize: 4096);

    private async Task ProcessConnectionAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_connectionTimeout);
        var connectionToken = deadline.Token;
        Guid messageId = Guid.Empty;
        StationMaterialArrivalLocalIpcResponse response;
        try
        {
            var payload = await ReadFrameAsync(
                    pipe,
                    _maximumRequestBytes,
                    connectionToken)
                .ConfigureAwait(false);
            var signal = JsonSerializer.Deserialize<StationMaterialArrivalSignal>(payload, JsonOptions)
                ?? throw new InvalidDataException("Material arrival IPC request is null.");
            messageId = signal.MessageId;
            var added = await _reporter.ReportAsync(signal, connectionToken)
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
                    connectionToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The local caller disconnected after submission; the durable outbox remains authoritative.
        }
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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _pipeName = string.IsNullOrWhiteSpace(options.PipeName)
        ? throw new ArgumentException("Material arrival IPC pipe name is required.", nameof(options))
        : options.PipeName;
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

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(connectTimeout);
        while (true)
        {
            try
            {
                return await ReportOnceAsync(payload, deadline.Token).ConfigureAwait(false);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                if (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Material arrival IPC did not complete before its connection deadline.");
                }

                try
                {
                    await Task.Delay(RetryDelay, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    deadline.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Material arrival IPC did not complete before its connection deadline.");
                }
            }
            catch (OperationCanceledException) when (
                deadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Material arrival IPC did not complete before its connection deadline.");
            }
        }
    }

    private async ValueTask<StationMaterialArrivalLocalIpcResponse> ReportOnceAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await StationMaterialArrivalLocalIpcServer
            .WriteFrameAsync(pipe, payload, cancellationToken)
            .ConfigureAwait(false);
        var responsePayload = await StationMaterialArrivalLocalIpcServer
            .ReadFrameAsync(pipe, 4096, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize<StationMaterialArrivalLocalIpcResponse>(
                   responsePayload,
                   JsonOptions)
               ?? throw new InvalidDataException("Material arrival IPC response is null.");
    }
}
