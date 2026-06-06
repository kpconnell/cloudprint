using CloudPrint.Service.Configuration;

namespace CloudPrint.Service.Devices;

/// <summary>
/// Maps a device type string to its <see cref="IDeviceReaderFactory"/>. The device-input analogue
/// of <see cref="Printing.PrintRouter"/>, resolved once per device at startup.
/// </summary>
public class DeviceReaderRegistry
{
    private readonly Dictionary<string, IDeviceReaderFactory> _factories;

    public DeviceReaderRegistry(IEnumerable<IDeviceReaderFactory> factories) =>
        _factories = factories.ToDictionary(f => f.DeviceType, StringComparer.OrdinalIgnoreCase);

    public IDeviceReader Create(ResolvedDevice device, IServiceProvider services)
    {
        if (!_factories.TryGetValue(device.Type, out var factory))
            throw new NotSupportedException(
                $"Unknown device type '{device.Type}'. Registered types: {string.Join(", ", _factories.Keys)}");

        return factory.Create(device, services);
    }
}
