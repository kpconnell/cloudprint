using System.Net.Sockets;

namespace CloudPrint.Service.Devices.Channels;

/// <summary>
/// TCP client channel for devices that act as a TCP server (Cubiscan :1050, Rice Lake iDimension,
/// Mettler TLD250 Ethernet, serial-device servers). Cross-platform. Reads never cancel the underlying
/// socket receive on timeout: a pending receive is parked and resumed on the next call, which keeps the
/// socket healthy and makes short read timeouts (idle-gap framing) cheap.
/// </summary>
public sealed class TcpByteChannel : IByteChannel
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly byte[] _rx = new byte[4096];
    private Task<int>? _pending;
    private int _leftoverPos, _leftoverLen; // bytes received but not yet handed to the caller

    private TcpByteChannel(TcpClient client, string host, int port)
    {
        _client = client;
        _stream = client.GetStream();
        Description = $"tcp {host}:{port}";
        var meta = new Dictionary<string, string> { ["host"] = host, ["port"] = port.ToString() };
        try
        {
            if (client.Client.RemoteEndPoint is { } rep) meta["remoteEndpoint"] = rep.ToString() ?? "";
            if (client.Client.LocalEndPoint is { } lep) meta["localEndpoint"] = lep.ToString() ?? "";
        }
        catch { /* endpoint info is best-effort */ }
        Metadata = meta;
    }

    public string Description { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public static async Task<TcpByteChannel> ConnectAsync(string host, int port, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(connectTimeout);
            await client.ConnectAsync(host, port, cts.Token);
            return new TcpByteChannel(client, host, port);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new IOException($"Connect to {host}:{port} timed out after {connectTimeout.TotalSeconds:0.#}s");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            client.Dispose();
            throw new IOException($"Connect to {host}:{port} failed: {ex.Message}", ex);
        }
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_leftoverLen > 0)
            return Serve(buffer);

        _pending ??= _stream.ReadAsync(_rx, 0, _rx.Length, CancellationToken.None);

        var completed = await Task.WhenAny(_pending, Task.Delay(timeout, cancellationToken));
        if (completed != _pending)
            return 0; // timeout; the receive stays parked

        var pending = _pending;
        _pending = null;
        int n;
        try
        {
            n = await pending;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new IOException($"{Description}: read failed ({ex.Message})", ex);
        }

        if (n == 0)
            throw new IOException($"{Description}: connection closed by peer");

        _leftoverPos = 0;
        _leftoverLen = n;
        return Serve(buffer);
    }

    private int Serve(Memory<byte> buffer)
    {
        var n = Math.Min(_leftoverLen, buffer.Length);
        _rx.AsSpan(_leftoverPos, n).CopyTo(buffer.Span);
        _leftoverPos += n;
        _leftoverLen -= n;
        return n;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await _stream.WriteAsync(data, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new IOException($"{Description}: write failed ({ex.Message})", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _stream.Dispose(); } catch { /* best effort */ }
        try { _client.Dispose(); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}
