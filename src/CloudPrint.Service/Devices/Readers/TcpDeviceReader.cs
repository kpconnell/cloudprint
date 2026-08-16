using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices.Channels;
using CloudPrint.Service.Devices.Parsing;

namespace CloudPrint.Service.Devices.Readers;

/// <summary>
/// TCP-client device factories (device is the server: Cubiscan :1050, Rice Lake iDimension, Mettler TLD250,
/// serial-device servers). Cross-platform — the same <see cref="FramedDeviceReader"/> as serial, over a
/// <see cref="TcpByteChannel"/>. Runs for real on macOS/Linux too, which is how it is integration-tested.
/// </summary>
public static class TcpReaders
{
    public static FramedDeviceReader Create(ResolvedDevice device, ISerialLineParser parser, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(device.Host) || device.Port <= 0)
            throw new ArgumentException($"TCP device '{device.Name}' requires Host and Port");

        return new FramedDeviceReader(device,
            async ct => await TcpByteChannel.ConnectAsync(device.Host!, device.Port, device.ConnectTimeout, ct),
            parser, logger);
    }
}

public class TcpScaleReaderFactory : IDeviceReaderFactory
{
    public string DeviceType => "tcp-scale";

    public IDeviceReader Create(ResolvedDevice device, IServiceProvider services) =>
        TcpReaders.Create(device, new SerialScaleParser(), services.GetRequiredService<ILogger<FramedDeviceReader>>());
}

public class RawTcpReaderFactory : IDeviceReaderFactory
{
    public string DeviceType => "tcp-raw";

    public IDeviceReader Create(ResolvedDevice device, IServiceProvider services) =>
        TcpReaders.Create(device, new PatternExtractor(device.Pattern), services.GetRequiredService<ILogger<FramedDeviceReader>>());
}
