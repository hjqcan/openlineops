using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions.Tests;

internal sealed record ScpiServerReply(
    string? Response = null,
    bool CloseWithoutResponse = false,
    TimeSpan? Delay = null);

internal sealed class LocalScpiServer : IAsyncDisposable
{
    private readonly Func<int, string, ScpiServerReply> _handler;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _serverTask;
    private int _connectionCount;

    public LocalScpiServer(Func<int, string, ScpiServerReply> handler)
    {
        _handler = handler;
        _listener.Start();
        _serverTask = RunAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public ConcurrentQueue<(int Connection, string Line)> Received { get; } = new();

    public async ValueTask WaitForReceivedCountAsync(
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
                $"Expected {expectedCount} received line(s), observed {Received.Count}.");
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
            // Expected while stopping the listener.
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
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            await using var writer = new StreamWriter(
                stream,
                Encoding.ASCII,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            while (!_lifetime.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(_lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }

                if (line is null)
                {
                    return;
                }

                Received.Enqueue((connection, line));
                var reply = _handler(connection, line);
                if (reply.Delay is { } delay)
                {
                    await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
                }

                if (reply.CloseWithoutResponse)
                {
                    return;
                }

                if (reply.Response is not null)
                {
                    await writer.WriteLineAsync(reply.Response.AsMemory(), _lifetime.Token)
                        .ConfigureAwait(false);
                }
            }
        }
    }
}

internal sealed record ScannerLine(string Value, TimeSpan DelayBefore);

internal sealed record ScannerConnectionScript(
    IReadOnlyList<ScannerLine> Lines,
    bool HoldOpen = false);

internal sealed class LocalScannerServer : IAsyncDisposable
{
    private readonly ConcurrentQueue<ScannerConnectionScript> _scripts;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _serverTask;
    private int _connectionCount;

    public LocalScannerServer(params ScannerConnectionScript[] scripts)
    {
        _scripts = new ConcurrentQueue<ScannerConnectionScript>(scripts);
        _listener.Start();
        _serverTask = RunAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

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
            // Expected during teardown.
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
            // Expected during teardown.
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

            Interlocked.Increment(ref _connectionCount);
            if (!_scripts.TryDequeue(out var script))
            {
                script = new ScannerConnectionScript([], HoldOpen: true);
            }

            await HandleClientAsync(client, script).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        ScannerConnectionScript script)
    {
        using (client)
        {
            await using var writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(false, true),
                leaveOpen: false)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            foreach (var line in script.Lines)
            {
                if (line.DelayBefore > TimeSpan.Zero)
                {
                    await Task.Delay(line.DelayBefore, _lifetime.Token)
                        .ConfigureAwait(false);
                }

                await writer.WriteLineAsync(line.Value.AsMemory(), _lifetime.Token)
                    .ConfigureAwait(false);
            }

            if (script.HoldOpen)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token)
                    .ConfigureAwait(false);
            }
        }
    }
}
