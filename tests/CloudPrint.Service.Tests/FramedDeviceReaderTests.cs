using System.Text;
using System.Threading.Channels;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices;
using CloudPrint.Service.Devices.Channels;
using CloudPrint.Service.Devices.Parsing;
using CloudPrint.Service.Devices.Readers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudPrint.Service.Tests;

/// <summary>Scripted in-memory channel: what the "device" sends, and a log of what we wrote to it.</summary>
internal sealed class FakeChannel : IByteChannel
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    public List<byte[]> Written { get; } = new();
    public bool Disposed { get; private set; }
    public string Description => "fake";
    public IReadOnlyDictionary<string, string> Metadata { get; } = new Dictionary<string, string> { ["fake"] = "yes" };

    public void DeviceSends(string text) => _incoming.Writer.TryWrite(Encoding.Latin1.GetBytes(text));
    public void DeviceSends(byte[] bytes) => _incoming.Writer.TryWrite(bytes);
    public void Die() => _incoming.Writer.TryComplete(new IOException("unplugged"));

    public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var chunk = await _incoming.Reader.ReadAsync(cts.Token);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return 0; }
        catch (ChannelClosedException ex) { throw new IOException("dead", ex); }
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        Written.Add(data.ToArray());
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

public class FramedDeviceReaderTests
{
    private static ResolvedDevice Device(Action<DeviceConfig> configure)
    {
        var c = new DeviceConfig { Name = "d", Type = "serial-raw", ReadTimeoutMs = 300 };
        configure(c);
        return new CloudPrintOptions { Devices = { c } }.ResolvedDevices()[0];
    }

    private static FramedDeviceReader Reader(ResolvedDevice device, FakeChannel channel, ISerialLineParser? parser = null) =>
        new(device, _ => Task.FromResult<IByteChannel>(channel), parser ?? new PatternExtractor(null), NullLogger.Instance);

    [Fact]
    public async Task Line_mode_raw_passthrough_carries_text_and_hex()
    {
        var ch = new FakeChannel();
        var reader = Reader(Device(c => c.LineEnding = "cr"), ch);
        await reader.ConnectAsync(default);
        ch.DeviceSends("\u000210.55\r");

        var r = await reader.ReadAsync(default);
        Assert.NotNull(r);
        Assert.Equal("\u000210.55", r!.Raw);
        Assert.Equal("0231302E3535", r.RawHex);
        Assert.Equal("ok", r.Status);
        Assert.True(r.Stable);
    }

    [Fact]
    public async Task Request_mode_sends_command_with_terminator_and_escapes()
    {
        var ch = new FakeChannel();
        var reader = Reader(Device(c =>
        {
            c.PollMode = "request"; c.PollIntervalMs = 1; c.RequestCommand = "<STX>M<ETX>"; c.LineEnding = "crlf";
            c.FrameMode = "delimited"; c.FrameStart = "<STX>"; c.FrameEnd = "<ETX>";
            c.InitCommands = new List<string> { @"\x02T\x03" };
        }), ch);
        await reader.ConnectAsync(default);
        Assert.Equal("\x02T\x03\r\n", Encoding.Latin1.GetString(ch.Written[0])); // init command + crlf terminator

        ch.DeviceSends("\x02MAH000000,L009.8,W007.2,H003.5,E,K000.00,D000.00,E,F0138,D\x03\r\n");
        var r = await reader.ReadAsync(default);
        Assert.Equal("\x02M\x03\r\n", Encoding.Latin1.GetString(ch.Written[1]));
        Assert.Equal("\x02MAH000000,L009.8,W007.2,H003.5,E,K000.00,D000.00,E,F0138,D\x03", r!.Raw);
    }

    [Fact]
    public async Task Command_terminator_none_sends_bare_command()
    {
        var ch = new FakeChannel();
        var reader = Reader(Device(c => { c.PollMode = "request"; c.PollIntervalMs = 1; c.RequestCommand = "W"; c.CommandTerminator = "none"; c.LineEnding = "cr"; }), ch);
        await reader.ConnectAsync(default);
        ch.DeviceSends("\u000210.55\r");
        await reader.ReadAsync(default);
        Assert.Equal("W", Encoding.Latin1.GetString(ch.Written[0]));
    }

    [Fact]
    public async Task Idle_mode_closes_frame_on_silence()
    {
        var ch = new FakeChannel();
        var reader = Reader(Device(c => { c.FrameMode = "idle"; c.IdleGapMs = 50; c.Encoding = "latin1"; }), ch);
        await reader.ConnectAsync(default);
        ch.DeviceSends(new byte[] { 0x02, (byte)',', (byte)' ', (byte)'1', (byte)'2', (byte)'3', (byte)'4', 0x0D, 0x9A });

        var r = await reader.ReadAsync(default);
        Assert.NotNull(r);
        Assert.Equal("022C20313233340D9A", r!.RawHex);
        Assert.Equal("\x02, 1234\r\u009A", r.Raw); // latin1 decodes 1:1, so even the checksum byte survives in text
    }

    [Fact]
    public async Task Multiple_frames_in_one_chunk_are_served_one_per_read()
    {
        var ch = new FakeChannel();
        var reader = Reader(Device(c => c.LineEnding = "crlf"), ch);
        await reader.ConnectAsync(default);
        ch.DeviceSends("A\r\nB\r\nC\r\n");
        Assert.Equal("A", (await reader.ReadAsync(default))!.Raw);
        Assert.Equal("B", (await reader.ReadAsync(default))!.Raw);
        Assert.Equal("C", (await reader.ReadAsync(default))!.Raw);
        Assert.Null(await reader.ReadAsync(default)); // timeout, nothing more
    }

    [Fact]
    public async Task Scale_parser_failure_still_forwards_frame_as_unparsed()
    {
        var ch = new FakeChannel();
        var reader = Reader(Device(c => { c.Type = "serial-scale"; c.LineEnding = "cr"; }), ch, new SerialScaleParser());
        await reader.ConnectAsync(default);
        ch.DeviceSends("\x02?a\r");
        var r = await reader.ReadAsync(default);
        Assert.NotNull(r);
        Assert.Equal("unparsed", r!.Status);
        Assert.Null(r.Value);
        Assert.Equal("023F61", r.RawHex);
    }

    [Fact]
    public async Task Dead_channel_surfaces_as_connection_exception_and_disconnects()
    {
        var ch = new FakeChannel();
        var reader = Reader(Device(_ => { }), ch);
        await reader.ConnectAsync(default);
        ch.Die();
        await Assert.ThrowsAsync<DeviceConnectionException>(() => reader.ReadAsync(default));
        Assert.False(reader.IsConnected);
        Assert.True(ch.Disposed);
    }

    [Fact]
    public async Task SendAsync_writes_exact_bytes()
    {
        var ch = new FakeChannel();
        var reader = Reader(Device(_ => { }), ch);
        await reader.ConnectAsync(default);
        await reader.SendAsync(new byte[] { 0x05 }, default);
        Assert.Equal(new byte[] { 0x05 }, ch.Written[0]);
        Assert.Contains("endpoint", reader.Metadata.Keys);
        Assert.Equal("yes", reader.Metadata["fake"]);
    }
}
