using System.Net;
using System.Net.Sockets;
using System.Text;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices;
using CloudPrint.Service.Devices.Parsing;
using CloudPrint.Service.Devices.Readers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudPrint.Service.Tests;

/// <summary>
/// End-to-end over a real socket: a tiny Cubiscan-style TCP server on loopback answers
/// &lt;STX&gt;M&lt;ETX&gt;&lt;CR&gt;&lt;LF&gt; with a measurement and &lt;STX&gt;T&lt;ETX&gt; with TA00. Exercises TcpByteChannel +
/// FramedDeviceReader exactly as the service would run them (this transport is cross-platform).
/// </summary>
public class TcpDeviceReaderTests
{
    private const string Measure = "\x02MAH000000,L009.8,W007.2,H003.5,E,K001.25,D000.00,E,F0138,D\x03\r\n";

    private static async Task<(TcpListener listener, Task server, List<string> received)> StartCubiscanServer(Func<string, string?> respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = new List<string>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[256];
            var pending = new StringBuilder();
            while (true)
            {
                int n;
                try { n = await stream.ReadAsync(buf); } catch { break; }
                if (n == 0) break;
                pending.Append(Encoding.Latin1.GetString(buf, 0, n));
                var text = pending.ToString();
                var idx = text.IndexOf('\n');
                if (idx < 0) continue;
                var cmd = text[..(idx + 1)];
                pending.Clear().Append(text[(idx + 1)..]);
                lock (received) received.Add(cmd);
                var reply = respond(cmd);
                if (reply is not null)
                {
                    var bytes = Encoding.Latin1.GetBytes(reply);
                    // deliberately split the reply into two writes to exercise reassembly
                    await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
                    await Task.Delay(20);
                    await stream.WriteAsync(bytes.AsMemory(bytes.Length / 2));
                }
            }
        });
        return (listener, server, received);
    }

    private static ResolvedDevice Device(int port, Action<DeviceConfig>? configure = null)
    {
        var c = new DeviceConfig
        {
            Name = "cubi", Type = "tcp-raw", Host = "127.0.0.1", Port = port,
            FrameMode = "delimited", FrameStart = "<STX>", FrameEnd = "<ETX>", LineEnding = "crlf",
            PollMode = "request", PollIntervalMs = 10, RequestCommand = "<STX>M<ETX>", ReadTimeoutMs = 1000,
            InitCommands = new List<string> { "<STX>T<ETX>" }
        };
        configure?.Invoke(c);
        return new CloudPrintOptions { Devices = { c } }.ResolvedDevices()[0];
    }

    [Fact]
    public async Task Polls_cubiscan_over_tcp_and_forwards_frames_verbatim()
    {
        var (listener, server, received) = await StartCubiscanServer(cmd =>
            cmd.StartsWith("\x02T") ? "\x02TA00\x03\r\n" : cmd.StartsWith("\x02M") ? Measure : "\x02?N\x03\r\n");
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var reader = TcpReaders.Create(Device(port), new PatternExtractor(null), NullLogger.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await reader.ConnectAsync(cts.Token);
            Assert.True(reader.IsConnected);
            Assert.Equal("tcp", reader.Source.Connection);
            Assert.Equal(port, reader.Source.TcpPort);
            Assert.Contains("remoteEndpoint", reader.Metadata.Keys);

            // First read returns the T ack (init command reply); the M reply follows.
            var first = await reader.ReadAsync(cts.Token);
            Assert.Equal("\x02TA00\x03", first!.Raw);
            Assert.Equal("025441303003", first.RawHex);

            var second = await reader.ReadAsync(cts.Token);
            Assert.Equal(Measure.TrimEnd('\r', '\n'), second!.Raw);

            lock (received)
            {
                Assert.Equal("\x02T\x03\r\n", received[0]);
                Assert.Equal("\x02M\x03\r\n", received[1]);
            }

            await reader.DisposeAsync();
            Assert.False(reader.IsConnected);
        }
        finally
        {
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Fact]
    public async Task Peer_close_becomes_connection_exception()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            await Task.Delay(100);
            // close without sending anything
        });
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var reader = TcpReaders.Create(Device(port, c => { c.PollMode = "stream"; c.InitCommands = null; }), new PatternExtractor(null), NullLogger.Instance);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await reader.ConnectAsync(cts.Token);
            await Assert.ThrowsAsync<DeviceConnectionException>(async () =>
            {
                for (var i = 0; i < 20; i++)
                    await reader.ReadAsync(cts.Token);
            });
            Assert.False(reader.IsConnected);
        }
        finally { listener.Stop(); try { await accept; } catch { } }
    }

    [Fact]
    public async Task Connect_refused_is_connection_exception()
    {
        // Grab a free port then release it so nothing listens there.
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();

        var reader = TcpReaders.Create(Device(port, c => c.ConnectTimeoutMs = 2000), new PatternExtractor(null), NullLogger.Instance);
        await Assert.ThrowsAsync<DeviceConnectionException>(() => reader.ConnectAsync(default));
        Assert.False(reader.IsConnected);
    }
}
