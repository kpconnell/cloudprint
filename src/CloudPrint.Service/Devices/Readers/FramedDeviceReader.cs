using System.Text;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices.Channels;
using CloudPrint.Service.Devices.Framing;
using CloudPrint.Service.Devices.Parsing;

namespace CloudPrint.Service.Devices.Readers;

/// <summary>
/// Transport-agnostic reader for byte-stream devices (serial COM ports, TCP sockets). Owns the request
/// cycle, init commands, framing (<see cref="FrameAssembler"/>) and text decoding; the channel factory
/// supplies the actual pipe. Every reading carries the exact frame as <c>rawHex</c> plus its text as
/// <c>raw</c>, so the cloud sees the device verbatim even when the optional parser makes nothing of it.
/// </summary>
public sealed class FramedDeviceReader : IDeviceReader
{
    private readonly ResolvedDevice _device;
    private readonly Func<CancellationToken, Task<IByteChannel>> _open;
    private readonly ISerialLineParser _parser;
    private readonly ILogger _logger;
    private readonly Encoding _encoding;
    private readonly byte[] _terminator;
    private readonly byte[] _rx = new byte[4096];

    private IByteChannel? _channel;
    private FrameAssembler _assembler;
    private Dictionary<string, string> _metadata = new();

    public FramedDeviceReader(
        ResolvedDevice device,
        Func<CancellationToken, Task<IByteChannel>> open,
        ISerialLineParser parser,
        ILogger logger)
    {
        _device = device;
        _open = open;
        _parser = parser;
        _logger = logger;
        _encoding = ResolveEncoding(device.Encoding);
        _terminator = Encoding.Latin1.GetBytes(device.EffectiveCommandTerminator);
        _assembler = new FrameAssembler(device.Framing);
    }

    public string DeviceId => _device.Name;
    public string DeviceType => _device.Type;
    public bool IsConnected => _channel is not null;
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    public ReadingSource Source => _device.IsTcp
        ? new ReadingSource { Connection = "tcp", Host = _device.Host, TcpPort = _device.Port }
        : new ReadingSource { Connection = "serial", Port = _device.ComPort };

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await Teardown();
        _assembler.Reset();

        IByteChannel channel;
        try
        {
            channel = await _open(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            throw new DeviceConnectionException($"Failed to open {_device.Type} device '{_device.Name}': {ex.Message}", ex);
        }

        _channel = channel;
        _metadata = new Dictionary<string, string>(channel.Metadata)
        {
            ["endpoint"] = channel.Description,
            ["frameMode"] = _device.Framing.Mode.ToString().ToLowerInvariant(),
            ["encoding"] = _device.Encoding
        };

        try
        {
            foreach (var command in _device.InitCommands)
                await WriteCommandAsync(command, cancellationToken);
        }
        catch (IOException ex)
        {
            await Teardown();
            throw new DeviceConnectionException($"Init command failed for '{_device.Name}': {ex.Message}", ex);
        }

        _logger.LogInformation("[device/{Name}] opened {Endpoint} (framing={Mode}, encoding={Enc})",
            _device.Name, channel.Description, _device.Framing.Mode, _device.Encoding);
    }

    public async Task<DeviceReading?> ReadAsync(CancellationToken cancellationToken)
    {
        var channel = _channel ?? throw new DeviceConnectionException($"Channel not open for '{_device.Name}'");

        // Frames left over from a previous chunk are served first.
        if (_assembler.TryDequeue(out var queued))
            return Decode(queued);

        try
        {
            if (_device.PollMode is "request" or "interval")
            {
                if (_device.PollIntervalMs > 0)
                    await Task.Delay(_device.PollIntervalMs, cancellationToken);
                if (!string.IsNullOrEmpty(_device.RequestCommand))
                    await WriteCommandAsync(_device.RequestCommand, cancellationToken);
            }

            // First read waits the full read timeout; once data starts flowing, follow-up reads wait only the
            // idle gap so idle-gap framing can close a frame and so a slow multi-chunk frame still completes.
            var timeout = _device.ReadTimeout;
            var deadline = DateTime.UtcNow + _device.ReadTimeout;
            while (true)
            {
                var n = await channel.ReadAsync(_rx, timeout, cancellationToken);
                if (n == 0)
                {
                    _assembler.Flush();
                    return _assembler.TryDequeue(out var flushed) ? Decode(flushed) : null;
                }

                _assembler.Push(_rx.AsSpan(0, n));
                if (_assembler.TryDequeue(out var frame))
                    return Decode(frame);

                if (DateTime.UtcNow >= deadline)
                    return null; // still mid-frame; yield so commands/cancellation get a turn

                timeout = _device.Framing.Mode == FrameMode.Idle ? _device.Framing.IdleGap : _device.ReadTimeout;
            }
        }
        catch (IOException ex)
        {
            await Teardown();
            throw new DeviceConnectionException($"Read failed for '{_device.Name}': {ex.Message}", ex);
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var channel = _channel ?? throw new DeviceConnectionException($"Channel not open for '{_device.Name}'");
        try
        {
            await channel.WriteAsync(payload, cancellationToken);
        }
        catch (IOException ex)
        {
            await Teardown();
            throw new DeviceConnectionException($"Write failed for '{_device.Name}': {ex.Message}", ex);
        }
    }

    /// <summary>Encodes a command (escapes already applied by config resolution or here) and appends the terminator.</summary>
    public byte[] EncodeCommand(string command)
    {
        var text = ControlEscapes.Unescape(command);
        var body = _encoding.GetBytes(text);
        if (_terminator.Length == 0) return body;
        var all = new byte[body.Length + _terminator.Length];
        body.CopyTo(all, 0);
        _terminator.CopyTo(all, body.Length);
        return all;
    }

    private async Task WriteCommandAsync(string command, CancellationToken cancellationToken)
    {
        var bytes = EncodeCommand(command);
        _logger.LogDebug("[device/{Name}] → {Bytes}", _device.Name, ControlEscapes.Describe(Encoding.Latin1.GetString(bytes)));
        await _channel!.WriteAsync(bytes, cancellationToken);
    }

    private DeviceReading? Decode(byte[] frame)
    {
        var text = _encoding.GetString(frame);
        var reading = _parser.TryParse(text);
        if (reading is null)
        {
            // The parser made nothing of it. Forward it anyway (status "unparsed") — for discovery the cloud
            // needs to see what the device actually said; only whitespace-only frames are dropped.
            if (string.IsNullOrWhiteSpace(text) && frame.All(b => b is 0x20 or 0x0D or 0x0A or 0x09 or 0x00))
                return null;
            reading = new DeviceReading { Raw = text.Trim(), Status = "unparsed", Stable = true };
        }
        reading.RawHex = Convert.ToHexString(frame);
        return reading;
    }

    private async Task Teardown()
    {
        var c = _channel;
        _channel = null;
        if (c is null) return;
        try { await c.DisposeAsync(); }
        catch { /* disposing a surprise-removed device can throw; the handle is gone either way */ }
    }

    public ValueTask DisposeAsync() => new(Teardown());

    private static Encoding ResolveEncoding(string encoding) => encoding switch
    {
        "utf8" or "utf-8" => Encoding.UTF8,
        "latin1" or "iso-8859-1" or "binary" => Encoding.Latin1,
        _ => Encoding.ASCII
    };
}
