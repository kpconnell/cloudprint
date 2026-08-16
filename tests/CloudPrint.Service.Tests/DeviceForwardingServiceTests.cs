using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices;
using CloudPrint.Service.Publishing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudPrint.Service.Tests;

public class DeviceForwardingServiceTests
{
    private static Mock<IDeviceReader> ConnectedReader()
    {
        var reader = new Mock<IDeviceReader>();
        var connected = false;
        reader.SetupGet(r => r.IsConnected).Returns(() => connected);
        reader.Setup(r => r.ConnectAsync(It.IsAny<CancellationToken>()))
            .Callback(() => connected = true).Returns(Task.CompletedTask);
        reader.SetupGet(r => r.DeviceId).Returns("dev1");
        reader.SetupGet(r => r.DeviceType).Returns("serial-scale");
        reader.SetupGet(r => r.Source).Returns(new ReadingSource { Connection = "serial", Port = "COM3" });
        reader.Setup(r => r.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return reader;
    }

    internal static ResolvedDevice Device(string station = "stn-1", bool stableOnly = true, Action<DeviceConfig>? configure = null)
    {
        var config = new DeviceConfig { Name = "dev1", Type = "serial-scale" };
        configure?.Invoke(config);
        return new CloudPrintOptions { Station = station, DeviceStableOnly = stableOnly, Devices = { config } }.ResolvedDevices()[0];
    }

    private static DeviceForwardingService Create(IDeviceReader reader, IReadingPublisher publisher,
        string station = "stn-1", bool stableOnly = true) =>
        new(reader, publisher, Device(station, stableOnly), NullLogger<DeviceForwardingService>.Instance);

    [Fact]
    public async Task Publishes_reading_and_stamps_identity()
    {
        var reader = ConnectedReader();
        var calls = 0;
        reader.Setup(r => r.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => calls++ == 0
                ? new DeviceReading { Value = 1.5m, Unit = "kg", Stable = true, Status = "ok" }
                : null);

        var publisher = new Mock<IReadingPublisher>();
        DeviceReading? published = null;
        publisher.Setup(p => p.PublishAsync(It.IsAny<DeviceReading>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceReading, CancellationToken>((r, _) => published = r).Returns(Task.CompletedTask);

        var service = Create(reader.Object, publisher.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        Assert.NotNull(published);
        Assert.Equal("stn-1", published!.Station);
        Assert.Equal(Environment.MachineName, published.Host);
        Assert.Equal("dev1", published.DeviceId);
        Assert.Equal("serial-scale", published.DeviceType);
        Assert.Equal("serial", published.Source.Connection);
    }

    [Fact]
    public async Task Drops_unstable_reading_when_stable_only()
    {
        var reader = ConnectedReader();
        reader.Setup(r => r.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DeviceReading { Value = 1m, Unit = "kg", Stable = false });

        var publisher = new Mock<IReadingPublisher>();

        var service = Create(reader.Object, publisher.Object, stableOnly: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await Task.Delay(400);
        await service.StopAsync(CancellationToken.None);

        // Only the lifecycle "connected" event may go out — never the unstable measurement.
        publisher.Verify(p => p.PublishAsync(It.Is<DeviceReading>(r => !r.IsEvent), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(p => p.PublishAsync(It.Is<DeviceReading>(r => r.Status == "connected"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Connection_failure_does_not_crash_loop()
    {
        var reader = new Mock<IDeviceReader>();
        reader.SetupGet(r => r.IsConnected).Returns(false);
        reader.Setup(r => r.ConnectAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DeviceConnectionException("device unplugged"));
        reader.Setup(r => r.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync((DeviceReading?)null);
        reader.Setup(r => r.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var publisher = new Mock<IReadingPublisher>();

        var service = Create(reader.Object, publisher.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await Task.Delay(400);
        await service.StopAsync(CancellationToken.None);

        reader.Verify(r => r.ConnectAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        reader.Verify(r => r.ReadAsync(It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(p => p.PublishAsync(It.IsAny<DeviceReading>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Read_disconnect_releases_handle_and_reconnects()
    {
        // Regression: after a mid-read disconnect the native handle can still look open
        // (e.g. SerialPort.IsOpen stays true after a USB unplug). The loop must drop the
        // handle and re-enter ConnectAsync rather than re-read the dead handle forever.
        var reader = new Mock<IDeviceReader>();
        var connected = false;
        reader.SetupGet(r => r.IsConnected).Returns(() => connected);
        reader.Setup(r => r.ConnectAsync(It.IsAny<CancellationToken>()))
            .Callback(() => connected = true).Returns(Task.CompletedTask);
        reader.Setup(r => r.DisposeAsync())
            .Callback(() => connected = false).Returns(ValueTask.CompletedTask);
        reader.SetupGet(r => r.DeviceId).Returns("dev1");
        reader.SetupGet(r => r.DeviceType).Returns("serial-scale");
        reader.SetupGet(r => r.Source).Returns(new ReadingSource { Connection = "serial", Port = "COM3" });

        var reads = 0;
        reader.Setup(r => r.ReadAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ++reads == 1
                ? Task.FromException<DeviceReading?>(new DeviceConnectionException("device unplugged"))
                : Task.FromResult<DeviceReading?>(null));

        var publisher = new Mock<IReadingPublisher>();
        var service = Create(reader.Object, publisher.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);
        await Task.Delay(2000); // first reconnect backoff step is 1s
        await service.StopAsync(CancellationToken.None);

        reader.Verify(r => r.DisposeAsync(), Times.AtLeastOnce);
        reader.Verify(r => r.ConnectAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
        Assert.True(reads >= 2, "loop should resume reading after reconnect");
    }

    [Fact]
    public async Task Publisher_exception_does_not_crash_loop()
    {
        var reader = ConnectedReader();
        var calls = 0;
        reader.Setup(r => r.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => calls++ == 0
                ? new DeviceReading { Value = 1.5m, Unit = "kg", Stable = true }
                : null);

        var publisher = new Mock<IReadingPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<DeviceReading>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("publish boom"));

        var service = Create(reader.Object, publisher.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        publisher.Verify(p => p.PublishAsync(It.IsAny<DeviceReading>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
