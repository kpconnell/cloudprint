#if WINDOWS
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices.Channels;
using CloudPrint.Service.Devices.Parsing;

namespace CloudPrint.Service.Devices.Readers;

/// <summary>
/// Serial (RS-232 / USB virtual COM) device factories. The reading logic lives in the transport-agnostic
/// <see cref="FramedDeviceReader"/>; these only bind it to a <see cref="SerialByteChannel"/>. Windows-only
/// (System.IO.Ports is referenced only on Windows builds).
/// </summary>
public static class SerialReaders
{
    public static FramedDeviceReader Create(ResolvedDevice device, ISerialLineParser parser, ILogger logger) =>
        new(device, _ => Task.FromResult<IByteChannel>(SerialByteChannel.Open(device)), parser, logger);
}

public class SerialScaleReaderFactory : IDeviceReaderFactory
{
    public string DeviceType => "serial-scale";

    public IDeviceReader Create(ResolvedDevice device, IServiceProvider services) =>
        SerialReaders.Create(device, new SerialScaleParser(), services.GetRequiredService<ILogger<FramedDeviceReader>>());
}

public class RawSerialReaderFactory : IDeviceReaderFactory
{
    public string DeviceType => "serial-raw";

    public IDeviceReader Create(ResolvedDevice device, IServiceProvider services) =>
        SerialReaders.Create(device, new PatternExtractor(device.Pattern), services.GetRequiredService<ILogger<FramedDeviceReader>>());
}
#endif
