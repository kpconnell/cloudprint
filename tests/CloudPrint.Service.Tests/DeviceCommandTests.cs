using System.Text;
using System.Text.Json;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices;
using CloudPrint.Service.Devices.Commands;
using CloudPrint.Service.Publishing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudPrint.Service.Tests;

public class DeviceCommandTests
{
    private static DeviceForwardingService Service(Mock<IDeviceReader> reader, Mock<IReadingPublisher> publisher, Action<DeviceConfig>? cfg = null) =>
        new(reader.Object, publisher.Object, DeviceForwardingServiceTests.Device(configure: cfg), NullLogger<DeviceForwardingService>.Instance);

    private static Mock<IDeviceReader> ConnectedReader()
    {
        var reader = new Mock<IDeviceReader>();
        var connected = false;
        reader.SetupGet(r => r.IsConnected).Returns(() => connected);
        reader.Setup(r => r.ConnectAsync(It.IsAny<CancellationToken>())).Callback(() => connected = true).Returns(Task.CompletedTask);
        reader.SetupGet(r => r.DeviceId).Returns("dev1");
        reader.SetupGet(r => r.DeviceType).Returns("tcp-raw");
        reader.SetupGet(r => r.Source).Returns(new ReadingSource { Connection = "tcp" });
        reader.SetupGet(r => r.Metadata).Returns(new Dictionary<string, string> { ["endpoint"] = "tcp 1.2.3.4:1050" });
        reader.Setup(r => r.DisposeAsync()).Callback(() => connected = false).Returns(ValueTask.CompletedTask);
        reader.Setup(r => r.SendAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return reader;
    }

    [Fact]
    public void Message_json_shape_round_trips()
    {
        var json = """{"id":"c1","device":"cubi","command":"<STX>M<ETX>","replyWindowMs":8000,"metadata":{"orderId":"42"}}""";
        var m = JsonSerializer.Deserialize<DeviceCommandMessage>(json)!;
        Assert.Equal("cubi", m.TargetDevice);
        Assert.Equal("<STX>M<ETX>", m.Command);
        Assert.Equal(8000, m.ReplyWindowMs);
        Assert.Equal("42", m.Metadata!["orderId"]);

        var alias = JsonSerializer.Deserialize<DeviceCommandMessage>("""{"deviceId":"scale-1","bytesBase64":"AgI="}""")!;
        Assert.Equal("scale-1", alias.TargetDevice);
    }

    [Fact]
    public void Resolve_applies_escapes_and_device_terminator()
    {
        var svc = Service(ConnectedReader(), new Mock<IReadingPublisher>(), c => c.LineEnding = "crlf");
        var cmd = svc.Resolve(new DeviceCommandMessage { Command = "<STX>M<ETX>" }, "c1");
        Assert.Equal("\x02M\x03\r\n", Encoding.Latin1.GetString(cmd.Payload));
        Assert.Equal("<STX>M<ETX><CR><LF>", cmd.Description);
        Assert.Equal(TimeSpan.FromSeconds(5), cmd.ReplyWindow);

        var bare = svc.Resolve(new DeviceCommandMessage { Command = "W", Terminator = "none" }, "c2");
        Assert.Equal("W", Encoding.Latin1.GetString(bare.Payload));

        var enq = svc.Resolve(new DeviceCommandMessage { Command = "<ENQ>", Terminator = "none", ReplyWindowMs = 250 }, "c3");
        Assert.Equal(new byte[] { 0x05 }, enq.Payload);
        Assert.Equal(TimeSpan.FromMilliseconds(250), enq.ReplyWindow);

        var b64 = svc.Resolve(new DeviceCommandMessage { BytesBase64 = Convert.ToBase64String(new byte[] { 0x02, 0x02 }) }, "c4");
        Assert.Equal(new byte[] { 0x02, 0x02 }, b64.Payload);

        Assert.Throws<ArgumentException>(() => svc.Resolve(new DeviceCommandMessage(), "c5"));
    }

    [Fact]
    public void Router_delivers_by_name_case_insensitively()
    {
        var router = new DeviceCommandRouter();
        var target = new Mock<IDeviceCommandTarget>();
        target.SetupGet(t => t.DeviceName).Returns("Cubi-1");
        target.Setup(t => t.TryEnqueue(It.IsAny<DeviceCommandMessage>())).Returns(true);
        router.Register(target.Object);

        Assert.True(router.TryRoute(new DeviceCommandMessage { Device = "cubi-1", Command = "T" }));
        Assert.False(router.TryRoute(new DeviceCommandMessage { Device = "nope", Command = "T" }));
        router.Unregister(target.Object);
        Assert.False(router.TryRoute(new DeviceCommandMessage { Device = "cubi-1", Command = "T" }));
    }

    [Fact]
    public async Task Command_is_sent_and_reply_is_correlated()
    {
        var reader = ConnectedReader();
        var sent = new List<byte[]>();
        reader.Setup(r => r.SendAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<byte>, CancellationToken>((b, _) => sent.Add(b.ToArray())).Returns(Task.CompletedTask);

        // Device answers only after something was sent.
        reader.Setup(r => r.ReadAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(50);
                return sent.Count > 0 && Interlocked.Exchange(ref _replied, 1) == 0
                    ? new DeviceReading { Raw = "\x02TA00\x03", Stable = true, Status = "ok" }
                    : null;
            });

        var published = new List<DeviceReading>();
        var publisher = new Mock<IReadingPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<DeviceReading>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceReading, CancellationToken>((r, _) => { lock (published) published.Add(r); }).Returns(Task.CompletedTask);

        _replied = 0;
        var svc = Service(reader, publisher, c => c.LineEnding = "crlf");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await Task.Delay(150);

        Assert.True(svc.TryEnqueue(new DeviceCommandMessage { Id = "cmd-9", Device = "dev1", Command = "<STX>T<ETX>", Metadata = new() { ["orderId"] = "42" } }));
        await Task.Delay(700);
        await svc.StopAsync(CancellationToken.None);

        Assert.Single(sent);
        Assert.Equal("\x02T\x03\r\n", Encoding.Latin1.GetString(sent[0]));

        DeviceReading[] snapshot;
        lock (published) snapshot = published.ToArray();
        Assert.Contains(snapshot, r => r.Status == "connected" && r.Metadata!["endpoint"] == "tcp 1.2.3.4:1050");
        var sentEvt = Assert.Single(snapshot, r => r.Status == "command-sent");
        Assert.Equal("cmd-9", sentEvt.Metadata!["commandId"]);
        Assert.Equal("0254030D0A", sentEvt.Metadata["bytesHex"]);
        Assert.Equal("42", sentEvt.Metadata["orderId"]);
        var reply = Assert.Single(snapshot, r => r.Raw == "\x02TA00\x03");
        Assert.Equal("cmd-9", reply.Metadata!["commandId"]);
    }
    private static int _replied;

    [Fact]
    public async Task Command_to_disconnected_device_publishes_failure()
    {
        var reader = new Mock<IDeviceReader>();
        reader.SetupGet(r => r.IsConnected).Returns(false);
        reader.Setup(r => r.ConnectAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new DeviceConnectionException("no such port"));
        reader.SetupGet(r => r.DeviceId).Returns("dev1");
        reader.SetupGet(r => r.DeviceType).Returns("serial-raw");
        reader.SetupGet(r => r.Source).Returns(new ReadingSource());
        reader.Setup(r => r.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var published = new List<DeviceReading>();
        var publisher = new Mock<IReadingPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<DeviceReading>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceReading, CancellationToken>((r, _) => { lock (published) published.Add(r); }).Returns(Task.CompletedTask);

        var svc = Service(reader, publisher);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        svc.TryEnqueue(new DeviceCommandMessage { Id = "c1", Command = "W" });
        await Task.Delay(300);
        await svc.StopAsync(CancellationToken.None);

        DeviceReading[] snapshot;
        lock (published) snapshot = published.ToArray();
        var failed = Assert.Single(snapshot, r => r.Status == "command-failed");
        Assert.Equal("c1", failed.Metadata!["commandId"]);
        Assert.Contains("not connected", failed.Metadata["error"]);
        reader.Verify(r => r.SendAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Disconnect_and_stale_events_are_published()
    {
        var reader = ConnectedReader();
        var reads = 0;
        reader.Setup(r => r.ReadAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(30);
                if (++reads == 3) throw new DeviceConnectionException("unplugged");
                return null;
            });

        var published = new List<DeviceReading>();
        var publisher = new Mock<IReadingPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<DeviceReading>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceReading, CancellationToken>((r, _) => { lock (published) published.Add(r); }).Returns(Task.CompletedTask);

        var svc = Service(reader, publisher, c => c.StaleAfterSeconds = 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await svc.StartAsync(cts.Token);
        await Task.Delay(2600); // disconnect at ~90ms, reconnect after 1s backoff, then >1s silence => stale
        await svc.StopAsync(CancellationToken.None);

        DeviceReading[] snapshot;
        lock (published) snapshot = published.ToArray();
        Assert.Contains(snapshot, r => r.Status == "disconnected" && r.Metadata!["error"].Contains("unplugged"));
        Assert.True(snapshot.Count(r => r.Status == "connected") >= 2);
        Assert.Contains(snapshot, r => r.Status == "stale");
    }
}
