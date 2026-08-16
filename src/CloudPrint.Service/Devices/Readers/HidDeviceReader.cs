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
    private Dictionary<string, string> _metadata = new();

    /// <summary>HID usage page 0x8D = Weighing Devices (HID POS scales), usage 0x20 = Scale Device.</summary>
    public const uint ScaleUsagePage = 0x8D;

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
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_device.Vid is null || _device.Pid is null)
            throw new DeviceConnectionException($"HID device '{_device.Name}' requires Vid and Pid");

        Teardown(); // release any stale handle from a previous connection before reopening

        // A composite device exposes one HID collection per interface; prefer the scale collection (usage
        // page 0x8D) so we don't open e.g. a keyboard-wedge collection of the same VID/PID.
        var hid = PickCollection(DeviceList.Local.GetHidDevices(_device.Vid, _device.Pid))
            ?? throw new DeviceConnectionException(
                $"HID device VID={_device.Vid:X4} PID={_device.Pid:X4} not found for '{_device.Name}'");

        if (!hid.TryOpen(out var stream))
            throw new DeviceConnectionException(
                $"Could not open HID device '{_device.Name}' (it may be exclusively claimed by another application)");

        stream.ReadTimeout = (int)Math.Max(100, _device.ReadTimeout.TotalMilliseconds);
        _stream = stream;
        _buffer = new byte[Math.Max(8, hid.GetMaxInputReportLength())];
        try { _product = hid.GetProductName(); } catch { _product = null; }
        _metadata = DescribeDevice(hid);

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

            var report = _buffer.AsSpan(0, count).ToArray();
            var reading = _decode(report);
            if (reading is null)
                return null;
            reading.RawHex ??= Convert.ToHexString(report);
            return reading;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The stream is dead after a read error (typically unplug) — release it so
            // IsConnected goes false and the forwarding loop reconnects.
            Teardown();
            throw new DeviceConnectionException($"HID read failed for '{_device.Name}'", ex);
        }
    }

    /// <summary>Writes an output report (e.g. HID-POS Zero Scale). Best effort — vendor support varies.</summary>
    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new DeviceConnectionException($"HID stream not open for '{_device.Name}'");
        try
        {
            stream.Write(payload.ToArray());
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Teardown();
            throw new DeviceConnectionException($"HID write failed for '{_device.Name}'", ex);
        }
    }

    /// <summary>Prefers the collection whose top-level usage is on the Weighing Devices page; otherwise the first match.</summary>
    public static HidDevice? PickCollection(IEnumerable<HidDevice> candidates)
    {
        HidDevice? first = null;
        foreach (var hid in candidates)
        {
            first ??= hid;
            if (UsagePages(hid).Contains(ScaleUsagePage))
                return hid;
        }
        return first;
    }

    /// <summary>Top-level usage pages of a HID collection (empty when the descriptor can't be read).</summary>
    public static IReadOnlyList<uint> UsagePages(HidDevice hid)
    {
        try
        {
            return hid.GetReportDescriptor().DeviceItems
                .SelectMany(item => item.Usages.GetAllValues())
                .Select(u => u >> 16)
                .Distinct()
                .ToList();
        }
        catch { return Array.Empty<uint>(); }
    }

    /// <summary>Everything worth knowing about a HID device for discovery: identity strings, usages, report sizes, descriptor.</summary>
    public static Dictionary<string, string> DescribeDevice(HidDevice hid)
    {
        var d = new Dictionary<string, string>
        {
            ["vid"] = hid.VendorID.ToString("X4"),
            ["pid"] = hid.ProductID.ToString("X4"),
            ["devicePath"] = hid.DevicePath
        };
        try { d["manufacturer"] = hid.GetManufacturer(); } catch { }
        try { d["product"] = hid.GetProductName(); } catch { }
        try { d["serial"] = hid.GetSerialNumber(); } catch { }
        try { d["maxInputReportLength"] = hid.GetMaxInputReportLength().ToString(); } catch { }
        try { d["maxOutputReportLength"] = hid.GetMaxOutputReportLength().ToString(); } catch { }
        try { d["maxFeatureReportLength"] = hid.GetMaxFeatureReportLength().ToString(); } catch { }
        try
        {
            var usages = hid.GetReportDescriptor().DeviceItems
                .SelectMany(item => item.Usages.GetAllValues())
                .Select(u => $"{u >> 16:X4}:{u & 0xFFFF:X4}")
                .Distinct().ToList();
            if (usages.Count > 0) d["usages"] = string.Join(",", usages);
            d["isScale"] = usages.Any(u => u.StartsWith("008D:", StringComparison.OrdinalIgnoreCase)) ? "true" : "false";
        }
        catch { }
        try { d["reportDescriptor"] = Convert.ToHexString(hid.GetRawReportDescriptor()); } catch { }
        foreach (var key in d.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            d.Remove(key); // HidSharp returns null for strings the device doesn't provide
        return d;
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
