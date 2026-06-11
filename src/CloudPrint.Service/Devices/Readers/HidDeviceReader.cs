#if WINDOWS
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices.Parsing;
using HidSharp;

namespace CloudPrint.Service.Devices.Readers;

/// <summary>
/// Reads input reports from a USB HID device (by VID/PID) via HidSharp and decodes them with the
/// supplied delegate. HID POS scales use the in-box Windows HID driver (no install). Opened in
/// shared mode; if the device is exclusively claimed (e.g. by a WinRT POS app), open fails and is
/// surfaced as a reconnectable <see cref="DeviceConnectionException"/>. Windows-only.
/// </summary>
public class HidDeviceReader : IDeviceReader
{
    private readonly ResolvedDevice _device;
    private readonly Func<byte[], DeviceReading?> _decode;
    private readonly ILogger _logger;

    private HidStream? _stream;
    private byte[] _buffer = Array.Empty<byte>();
    private string? _product;

    public HidDeviceReader(ResolvedDevice device, Func<byte[], DeviceReading?> decode, ILogger logger)
    {
        _device = device;
        _decode = decode;
        _logger = logger;
    }

    public string DeviceId => _device.Name;
    public string DeviceType => _device.Type;
    public ReadingSource Source => new() { Connection = "hid", Vid = _device.Vid, Pid = _device.Pid, Product = _product };
    public bool IsConnected => _stream is not null;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_device.Vid is null || _device.Pid is null)
            throw new DeviceConnectionException($"HID device '{_device.Name}' requires Vid and Pid");

        Teardown(); // release any stale handle from a previous connection before reopening

        var hid = DeviceList.Local.GetHidDevices(_device.Vid, _device.Pid).FirstOrDefault()
            ?? throw new DeviceConnectionException(
                $"HID device VID={_device.Vid:X4} PID={_device.Pid:X4} not found for '{_device.Name}'");

        if (!hid.TryOpen(out var stream))
            throw new DeviceConnectionException(
                $"Could not open HID device '{_device.Name}' (it may be exclusively claimed by another application)");

        stream.ReadTimeout = 2000;
        _stream = stream;
        _buffer = new byte[Math.Max(8, hid.GetMaxInputReportLength())];
        try { _product = hid.GetProductName(); } catch { _product = null; }

        _logger.LogInformation("[device/{Name}] opened HID device VID={Vid:X4} PID={Pid:X4} ({Product})",
            _device.Name, _device.Vid, _device.Pid, _product ?? "unknown");

        return Task.CompletedTask;
    }

    public async Task<DeviceReading?> ReadAsync(CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null)
            throw new DeviceConnectionException($"HID stream not open for '{_device.Name}'");

        try
        {
            var count = await Task.Run(() =>
            {
                try { return stream.Read(_buffer, 0, _buffer.Length); }
                catch (TimeoutException) { return 0; }
            }, cancellationToken);

            if (count <= 0)
                return null;

            return _decode(_buffer.AsSpan(0, count).ToArray());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The stream is dead after a read error (typically unplug) — release it so
            // IsConnected goes false and the forwarding loop reconnects.
            Teardown();
            throw new DeviceConnectionException($"HID read failed for '{_device.Name}'", ex);
        }
    }

    private void Teardown()
    {
        try { _stream?.Dispose(); }
        catch { /* disposing a surprise-removed device can throw; the handle is gone either way */ }
        _stream = null;
    }

    public ValueTask DisposeAsync()
    {
        Teardown();
        return ValueTask.CompletedTask;
    }
}

public class HidScaleReaderFactory : IDeviceReaderFactory
{
    public string DeviceType => "hid-scale";

    public IDeviceReader Create(ResolvedDevice device, IServiceProvider services)
    {
        var map = HidFieldMap.Default with
        {
            ReportId = (byte?)device.Hid.ReportId,
            StatusOffset = device.Hid.StatusOffset ?? HidFieldMap.Default.StatusOffset,
            UnitOffset = device.Hid.UnitOffset ?? HidFieldMap.Default.UnitOffset,
            ExponentOffset = device.Hid.ExponentOffset ?? HidFieldMap.Default.ExponentOffset,
            WeightOffset = device.Hid.WeightOffset ?? HidFieldMap.Default.WeightOffset,
            WeightSize = device.Hid.WeightSize ?? HidFieldMap.Default.WeightSize
        };

        var parser = new HidPosReportParser();
        return new HidDeviceReader(device, report => parser.TryParse(report, map),
            services.GetRequiredService<ILogger<HidDeviceReader>>());
    }
}

public class RawHidReaderFactory : IDeviceReaderFactory
{
    public string DeviceType => "hid-raw";

    public IDeviceReader Create(ResolvedDevice device, IServiceProvider services) =>
        new HidDeviceReader(device,
            report => new DeviceReading { Raw = Convert.ToHexString(report), Status = "ok", Stable = true },
            services.GetRequiredService<ILogger<HidDeviceReader>>());
}
#endif
