using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions.Tests;

internal sealed record ModbusRequestFrame(
    int Connection,
    ushort TransactionId,
    ushort ProtocolId,
    ushort Length,
    byte UnitId,
    byte[] Pdu);

internal sealed record ModbusServerReply(
    byte[]? ResponseFrame = null,
    bool CloseWithoutResponse = false,
    TimeSpan? Delay = null,
    IReadOnlyList<int>? FragmentSizes = null);

internal static class ModbusTestFrames
{
    public static byte[] Response(
        ModbusRequestFrame request,
        byte[] responsePdu,
        ushort? transactionId = null,
        ushort? protocolId = null,
        byte? unitId = null,
        ushort? declaredLength = null)
    {
        var frame = new byte[7 + responsePdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.AsSpan(0, 2),
            transactionId ?? request.TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.AsSpan(2, 2),
            protocolId ?? 0);
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.AsSpan(4, 2),
            declaredLength ?? checked((ushort)(responsePdu.Length + 1)));
        frame[6] = unitId ?? request.UnitId;
        responsePdu.CopyTo(frame, 7);
        return frame;
    }

    public static byte[] EchoWriteResponse(ModbusRequestFrame request)
    {
        var responsePdu = request.Pdu[0] is 0x05 or 0x06
            ? request.Pdu.ToArray()
            : request.Pdu[..5];
        return Response(request, responsePdu);
    }
}

internal sealed class LocalModbusTcpServer : IAsyncDisposable
{
    private const int MbapHeaderLength = 7;
    private readonly Func<int, ModbusRequestFrame, ModbusServerReply> _handler;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _serverTask;
    private int _connectionCount;
    private int _requestCount;

    public LocalModbusTcpServer(
        Func<int, ModbusRequestFrame, ModbusServerReply> handler)
    {
        _handler = handler;
        _listener.Start();
        _serverTask = RunAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public ConcurrentQueue<ModbusRequestFrame> Received { get; } = new();

    public async ValueTask WaitForRequestCountAsync(
        int expectedCount,
        TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(
            timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            while (Received.Count < expectedCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Expected {expectedCount} Modbus request(s), observed {Received.Count}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected while stopping the server.
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
            // Expected while stopping the listener.
        }

        _lifetime.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_lifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }

            var connection = Interlocked.Increment(ref _connectionCount);
            await HandleClientAsync(client, connection).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(TcpClient client, int connection)
    {
        using (client)
        {
            var stream = client.GetStream();
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    var header = new byte[MbapHeaderLength];
                    if (!await TryReadExactlyAsync(
                            stream,
                            header,
                            _lifetime.Token)
                        .ConfigureAwait(false))
                    {
                        return;
                    }

                    var length = BinaryPrimitives.ReadUInt16BigEndian(
                        header.AsSpan(4, 2));
                    if (length < 2)
                    {
                        return;
                    }

                    var pdu = new byte[length - 1];
                    if (!await TryReadExactlyAsync(
                            stream,
                            pdu,
                            _lifetime.Token)
                        .ConfigureAwait(false))
                    {
                        return;
                    }

                    var frame = new ModbusRequestFrame(
                        connection,
                        BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)),
                        length,
                        header[6],
                        pdu);
                    Received.Enqueue(frame);
                    var requestNumber = Interlocked.Increment(ref _requestCount);
                    var reply = _handler(requestNumber, frame);
                    if (reply.Delay is { } delay)
                    {
                        await Task.Delay(delay, _lifetime.Token)
                            .ConfigureAwait(false);
                    }

                    if (reply.CloseWithoutResponse)
                    {
                        return;
                    }

                    if (reply.ResponseFrame is not null)
                    {
                        await WriteResponseAsync(
                                stream,
                                reply.ResponseFrame,
                                reply.FragmentSizes,
                                _lifetime.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_lifetime
                    .IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException
                                                   or SocketException
                                                   or ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    private static async ValueTask WriteResponseAsync(
        Stream stream,
        byte[] response,
        IReadOnlyList<int>? fragmentSizes,
        CancellationToken cancellationToken)
    {
        if (fragmentSizes is null || fragmentSizes.Count == 0)
        {
            await stream.WriteAsync(response, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var offset = 0;
        foreach (var requestedSize in fragmentSizes)
        {
            if (requestedSize <= 0 || offset >= response.Length)
            {
                continue;
            }

            var size = Math.Min(requestedSize, response.Length - offset);
            await stream.WriteAsync(
                    response.AsMemory(offset, size),
                    cancellationToken)
                .ConfigureAwait(false);
            offset += size;
            await Task.Delay(
                    TimeSpan.FromMilliseconds(2),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (offset < response.Length)
        {
            await stream.WriteAsync(
                    response.AsMemory(offset),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<bool> TryReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var received = await stream.ReadAsync(
                    buffer[offset..],
                    cancellationToken)
                .ConfigureAwait(false);
            if (received == 0)
            {
                return false;
            }

            offset += received;
        }

        return true;
    }
}
