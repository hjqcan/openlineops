using System.Net.Sockets;
using System.Text;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

internal sealed class TcpLineTransport : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _connectTimeout;
    private readonly Encoding _encoding;
    private readonly string _newLine;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public TcpLineTransport(
        string host,
        int port,
        int connectTimeoutMilliseconds,
        string encoding,
        string lineTerminator)
    {
        _host = host;
        _port = port;
        _connectTimeout = TimeSpan.FromMilliseconds(connectTimeoutMilliseconds);
        _encoding = encoding switch
        {
            "ascii" => Encoding.ASCII,
            "utf8" => new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            _ => throw new InvalidDataException($"Unsupported encoding '{encoding}'.")
        };
        _newLine = lineTerminator switch
        {
            "lf" => "\n",
            "crlf" => "\r\n",
            "cr" => "\r",
            _ => throw new InvalidDataException(
                $"Unsupported line terminator '{lineTerminator}'.")
        };
    }

    public bool IsConnected => _client?.Connected == true;

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
            var stream = client.GetStream();
            _client = client;
            _reader = new StreamReader(
                stream,
                _encoding,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4_096,
                leaveOpen: true);
            _writer = new StreamWriter(
                stream,
                _encoding,
                bufferSize: 4_096,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = _newLine
            };
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async ValueTask WriteLineAsync(
        string value,
        CancellationToken cancellationToken)
    {
        var writer = _writer
            ?? throw new IOException("TCP transport is not connected.");
        await writer.WriteLineAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var reader = _reader
            ?? throw new IOException("TCP transport is not connected.");
        return await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The remote device closed the TCP connection.");
    }

    public ValueTask DisposeAsync() => DisconnectAsync();

    public ValueTask DisconnectAsync()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _client?.Dispose();
        _writer = null;
        _reader = null;
        _client = null;
        return ValueTask.CompletedTask;
    }
}
