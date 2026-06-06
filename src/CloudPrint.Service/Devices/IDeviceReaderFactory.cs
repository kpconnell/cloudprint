using CloudPrint.Service.Configuration;

namespace CloudPrint.Service.Devices;

/// <summary>
/// Creates an <see cref="IDeviceReader"/> for a given device type. One factory per device type;
/// the registry resolves the configured type string to a factory. Adding a new device type means
/// adding a reader + factory and registering the factory once — nothing else changes.
/// </summary>
public interface IDeviceReaderFactory
{
    /// <summary>The device type discriminator this factory handles (e.g. "serial-scale").</summary>
    string DeviceType { get; }

    IDeviceReader Create(ResolvedDevice device, IServiceProvider services);
}
