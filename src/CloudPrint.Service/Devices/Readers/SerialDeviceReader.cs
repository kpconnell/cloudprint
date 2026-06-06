#if WINDOWS
using System.IO.Ports;
using System.Text;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices.Parsing;

namespace CloudPrint.Service.Devices.Readers;

/// <summary>
/// Reads delimited ASCII frames from a serial / USB-CDC device (COM port) and parses them with the
/// supplied <see cref="ISerialLineParser"/>. Honors per-device line ending, encoding, request command,
/// and init commands. Windows-only (System.IO.Ports is referenced only on Windows builds).
/// </summary>
public class SerialDeviceReader : IDeviceReader
{
    private readonly ResolvedDevice _device;
    private readonly ISerialLineParser _parser;
    private readonly ILogger _logger;
    private SerialPort? _port;

    public SerialDeviceReader(ResolvedDevice device, ISerialLineParser parser, ILogger logger)
    {
        _device = device;
        _parser = parser;
        _logger = logger;
    }

    public string DeviceId => _device.Name;
    public string DeviceType => _device.Type;
    public ReadingSource Source => new() { Connection = "serial", Port = _device.ComPort };
    public bool IsConnected => _port?.IsOpen ?? false;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_device.ComPort))
            throw new DeviceConnectionException($"Device '{_device.Name}' has no ComPort configured");

        try
        {
            var port = new SerialPort(_device.ComPort, _device.BaudRate, ParseParity(_device.Parity),
                _device.DataBits, ParseStopBits(_device.StopBits))
            {
                ReadTimeout = 2000,
                WriteTimeout = 2000,
                NewLine = ResolveLineEnding(_device.LineEnding),
                Encoding = ResolveEncoding(_device.Encoding)
            };

            port.Open();
            _port = port;

            foreach (var command in _device.InitCommands)
                _port.Write(command + _port.NewLine);

            _logger.LogInformation("[device/{Name}] opened serial port {Port} ({Baud} baud)",
                _device.Name, _device.ComPort, _device.BaudRate);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       or ArgumentException or InvalidOperationException)
        {
            throw new DeviceConnectionException(
                $"Failed to open serial port {_device.ComPort} for '{_device.Name}'", ex);
        }

        return Task.CompletedTask;
    }

    public async Task<DeviceReading?> ReadAsync(CancellationToken cancellationToken)
    {
        var port = _port;
        if (port is null || !port.IsOpen)
            throw new DeviceConnectionException($"Serial port not open for '{_device.Name}'");

        try
        {
            if (_device.PollMode is "request" or "interval")
            {
                if (_device.PollIntervalMs > 0)
                    await Task.Delay(_device.PollIntervalMs, cancellationToken);
                if (!string.IsNullOrEmpty(_device.RequestCommand))
                    port.Write(_device.RequestCommand + port.NewLine);
            }

            var line = await ReadLineAsync(port, cancellationToken);
            return line is null ? null : _parser.TryParse(line);
        }
        catch (TimeoutException)
        {
            return null; // no data this cycle
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new DeviceConnectionException($"Serial read failed for '{_device.Name}'", ex);
        }
    }

    private static Task<string?> ReadLineAsync(SerialPort port, CancellationToken cancellationToken) =>
        // SerialPort has no async read; ReadTimeout bounds the blocking call.
        Task.Run(() =>
        {
            try { return (string?)port.ReadLine(); }
            catch (TimeoutException) { return null; }
        }, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }

    private static Parity ParseParity(string parity) => parity.ToLowerInvariant() switch
    {
        "even" => Parity.Even,
        "odd" => Parity.Odd,
        "mark" => Parity.Mark,
        "space" => Parity.Space,
        _ => Parity.None
    };

    private static StopBits ParseStopBits(int stopBits) => stopBits switch
    {
        2 => StopBits.Two,
        _ => StopBits.One
    };

    private static string ResolveLineEnding(string lineEnding) => lineEnding switch
    {
        "crlf" => "\r\n",
        "lf" => "\n",
        "cr" => "\r",
        _ => lineEnding // literal terminator
    };

    private static Encoding ResolveEncoding(string encoding) => encoding switch
    {
        "utf8" => Encoding.UTF8,
        _ => Encoding.ASCII
    };
}

public class SerialScaleReaderFactory : IDeviceReaderFactory
{
    public string DeviceType => "serial-scale";

    public IDeviceReader Create(ResolvedDevice device, IServiceProvider services) =>
        new SerialDeviceReader(device, new SerialScaleParser(),
            services.GetRequiredService<ILogger<SerialDeviceReader>>());
}

public class RawSerialReaderFactory : IDeviceReaderFactory
{
    public string DeviceType => "serial-raw";

    public IDeviceReader Create(ResolvedDevice device, IServiceProvider services) =>
        new SerialDeviceReader(device, new PatternExtractor(device.Pattern),
            services.GetRequiredService<ILogger<SerialDeviceReader>>());
}
#endif
