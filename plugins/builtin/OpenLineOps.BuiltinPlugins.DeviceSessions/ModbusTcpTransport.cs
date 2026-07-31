using System.Buffers.Binary;
using System.Net.Sockets;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

internal sealed class ModbusTcpTransport : IAsyncDisposable
{
    internal const int MaximumPduLength = 253;
    private const int MbapHeaderLength = 7;
    private const ushort ModbusProtocolIdentifier = 0;
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _connectTimeout;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public ModbusTcpTransport(
        string host,
        int port,
        int connectTimeoutMilliseconds)
    {
        _host = host;
        _port = port;
        _connectTimeout = TimeSpan.FromMilliseconds(connectTimeoutMilliseconds);
    }

    public bool IsConnected => _stream is not null;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        await DisconnectAsync().ConfigureAwait(false);
        var client = new TcpClient
        {
            NoDelay = true
        };
        using var timeout = new CancellationTokenSource(_connectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            cancellationToken);
        try
        {
            await client.ConnectAsync(_host, _port, linked.Token).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested
                                                            && !cancellationToken
                                                                .IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException(
                $"Connecting to Modbus TCP endpoint {_host}:{_port} timed out.",
                exception);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async ValueTask<byte[]> ExchangeAsync(
        ushort transactionId,
        byte unitId,
        ReadOnlyMemory<byte> requestPdu,
        CancellationToken cancellationToken)
    {
        if (requestPdu.Length is < 1 or > MaximumPduLength)
        {
            throw new InvalidDataException(
                $"A Modbus PDU must contain between 1 and {MaximumPduLength} bytes.");
        }

        var stream = _stream ?? throw new IOException("Modbus TCP transport is not connected.");
        var request = new byte[MbapHeaderLength + requestPdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(
            request.AsSpan(2, 2),
            ModbusProtocolIdentifier);
        BinaryPrimitives.WriteUInt16BigEndian(
            request.AsSpan(4, 2),
            checked((ushort)(requestPdu.Length + 1)));
        request[6] = unitId;
        requestPdu.Span.CopyTo(request.AsSpan(MbapHeaderLength));

        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var header = new byte[MbapHeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var responseTransactionId = BinaryPrimitives.ReadUInt16BigEndian(
            header.AsSpan(0, 2));
        var responseProtocolId = BinaryPrimitives.ReadUInt16BigEndian(
            header.AsSpan(2, 2));
        var responseLength = BinaryPrimitives.ReadUInt16BigEndian(
            header.AsSpan(4, 2));
        var responseUnitId = header[6];

        if (responseTransactionId != transactionId)
        {
            throw new ModbusProtocolException(
                $"Response transaction ID {responseTransactionId} does not match request {transactionId}.");
        }

        if (responseProtocolId != ModbusProtocolIdentifier)
        {
            throw new ModbusProtocolException(
                $"Response protocol ID {responseProtocolId} is invalid; Modbus TCP requires 0.");
        }

        if (responseUnitId != unitId)
        {
            throw new ModbusProtocolException(
                $"Response unit ID {responseUnitId} does not match request {unitId}.");
        }

        // MBAP length includes the unit identifier plus a PDU of 1..253 bytes.
        if (responseLength is < 2 or > MaximumPduLength + 1)
        {
            throw new ModbusProtocolException(
                $"Response MBAP length {responseLength} is outside the valid range 2..254.");
        }

        var responsePdu = new byte[responseLength - 1];
        await ReadExactlyAsync(stream, responsePdu, cancellationToken)
            .ConfigureAwait(false);
        return responsePdu;
    }

    public ValueTask DisposeAsync() => DisconnectAsync();

    public ValueTask DisconnectAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return ValueTask.CompletedTask;
    }

    private static async ValueTask ReadExactlyAsync(
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
                throw new IOException(
                    "The remote device closed the Modbus TCP connection before the response frame was complete.");
            }

            offset += received;
        }
    }
}

internal sealed class ModbusProtocolException : IOException
{
    public ModbusProtocolException(string message)
        : base(message)
    {
    }
}

internal sealed class ModbusDeviceException : Exception
{
    public ModbusDeviceException(byte functionCode, byte exceptionCode)
        : base(
            $"Modbus function 0x{functionCode:X2} returned exception 0x{exceptionCode:X2} ({Describe(exceptionCode)}).")
    {
        FunctionCode = functionCode;
        ExceptionCode = exceptionCode;
    }

    public byte FunctionCode { get; }

    public byte ExceptionCode { get; }

    private static string Describe(byte exceptionCode) =>
        exceptionCode switch
        {
            0x01 => "IllegalFunction",
            0x02 => "IllegalDataAddress",
            0x03 => "IllegalDataValue",
            0x04 => "ServerDeviceFailure",
            0x05 => "Acknowledge",
            0x06 => "ServerDeviceBusy",
            0x08 => "MemoryParityError",
            0x0A => "GatewayPathUnavailable",
            0x0B => "GatewayTargetFailedToRespond",
            _ => "UnknownException"
        };
}
