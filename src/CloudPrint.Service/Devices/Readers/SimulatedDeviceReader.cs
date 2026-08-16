using CloudPrint.Service.Configuration;

namespace CloudPrint.Service.Devices.Readers;

/// <summary>
/// Emits synthetic readings on an interval. Used on non-Windows and in --dry-run so the full
/// device pipeline (reader → forwarding service → publisher) runs without hardware. The device
/// analogue of <see cref="Printing.DryRunPrinter"/>.
/// </summary>
public class SimulatedDeviceReader : IDeviceReader
{
    private readonly ResolvedDevice _device;
    private readonly ILogger<SimulatedDeviceReader> _logger;
    private int _tick;

    public SimulatedDeviceReader(ResolvedDevice device, ILogger<SimulatedDeviceReader> logger)
    {
        _device = device;
        _logger = logger;
    }

    public string DeviceId => _device.Name;
    public string DeviceType => _device.Type;
    public ReadingSource Source => new() { Connection = "simulated", Port = _device.ComPort, Vid = _device.Vid, Pid = _device.Pid, Host = _device.Host, TcpPort = _device.Port > 0 ? _device.Port : null };
    public bool IsConnected { get; private set; }
    public IReadOnlyDictionary<string, string> Metadata { get; } = new Dictionary<string, string> { ["endpoint"] = "simulated" };

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        _logger.LogInformation("[device/{Name}] simulated device connected", _device.Name);
        return Task.CompletedTask;
    }

    public async Task<DeviceReading?> ReadAsync(CancellationToken cancellationToken)
    {
        var interval = _device.PollIntervalMs > 0 ? _device.PollIntervalMs : 500;
        await Task.Delay(interval, cancellationToken);

        _tick++;
        // A changing stable weight so dedup periodically lets a reading through.
        var weight = 1.50m + (_tick % 5) * 0.25m;
        return new DeviceReading
        {
            Value = weight,
            Unit = "kg",
            Stable = true,
            Status = "ok",
            Raw = $"SIM ST {weight} kg",
            RawHex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes($"SIM ST {weight} kg"))
        };
    }

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[device/{Name}] simulated device received {Bytes} byte(s): {Hex}",
            _device.Name, payload.Length, Convert.ToHexString(payload.Span));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}

public class SimulatedDeviceReaderFactory : IDeviceReaderFactory
{
    public string DeviceType => "simulated";

    public IDeviceReader Create(ResolvedDevice device, IServiceProvider services) =>
        new SimulatedDeviceReader(device, services.GetRequiredService<ILogger<SimulatedDeviceReader>>());
}
