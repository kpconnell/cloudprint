namespace CloudPrint.Service.Devices;

/// <summary>
/// Reads telemetry from a single physical device. Owns connect, the read loop, and parsing.
/// The outbound analogue of <see cref="Transport.IJobSource"/>. Implementations hold native
/// handles (serial port / HID stream), hence <see cref="IAsyncDisposable"/>.
/// </summary>
public interface IDeviceReader : IAsyncDisposable
{
    /// <summary>Configured device name.</summary>
    string DeviceId { get; }

    /// <summary>The device type discriminator (serial-scale, hid-scale, ...).</summary>
    string DeviceType { get; }

    /// <summary>Physical origin descriptor stamped onto each reading.</summary>
    ReadingSource Source { get; }

    bool IsConnected { get; }

    /// <summary>Open/claim the device and run any init commands. Throws <see cref="DeviceConnectionException"/> on failure.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Returns the next reading, or null if nothing was available this cycle.</summary>
    Task<DeviceReading?> ReadAsync(CancellationToken cancellationToken);
}
