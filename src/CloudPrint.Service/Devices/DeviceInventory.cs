using System.Text.Json.Serialization;

namespace CloudPrint.Service.Devices;

/// <summary>
/// What is plugged into this machine, as reported by <c>cloudprint list-devices --json</c>. The
/// configurator uses it for its pickers; the same facts are stamped on "connected" events.
/// </summary>
public sealed class DeviceInventory
{
    [JsonPropertyName("serialPorts")]
    public List<SerialPortInfo> SerialPorts { get; set; } = new();

    [JsonPropertyName("hidDevices")]
    public List<HidDeviceInfo> HidDevices { get; set; } = new();

    public sealed class SerialPortInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("friendlyName")] public string? FriendlyName { get; set; }
        [JsonPropertyName("vid")] public string? Vid { get; set; }          // hex, e.g. "0403"
        [JsonPropertyName("pid")] public string? Pid { get; set; }
        [JsonPropertyName("serial")] public string? Serial { get; set; }
        [JsonPropertyName("enumerator")] public string? Enumerator { get; set; } // USB, FTDIBUS, ACPI...
    }

    public sealed class HidDeviceInfo
    {
        [JsonPropertyName("vid")] public string Vid { get; set; } = string.Empty; // hex
        [JsonPropertyName("pid")] public string Pid { get; set; } = string.Empty;
        [JsonPropertyName("product")] public string? Product { get; set; }
        [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
        [JsonPropertyName("serial")] public string? Serial { get; set; }
        [JsonPropertyName("usages")] public string? Usages { get; set; }     // "008D:0020,..."
        [JsonPropertyName("isScale")] public bool IsScale { get; set; }
        [JsonPropertyName("devicePath")] public string? DevicePath { get; set; }
        [JsonPropertyName("maxInputReportLength")] public int? MaxInputReportLength { get; set; }
    }

#if WINDOWS
    public static DeviceInventory Collect()
    {
        var inv = new DeviceInventory();

        var identities = Channels.SerialPortLocator.Enumerate();
        var friendlyByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var sd in HidSharp.DeviceList.Local.GetSerialDevices())
            {
                string? name = null, friendly = null;
                try { name = sd.GetFileSystemName(); } catch { }
                try { friendly = sd.GetFriendlyName(); } catch { }
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(friendly))
                    friendlyByName[name] = friendly;
            }
        }
        catch { /* best effort */ }

        foreach (var name in System.IO.Ports.SerialPort.GetPortNames().Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var id = identities.FirstOrDefault(p => string.Equals(p.PortName, name, StringComparison.OrdinalIgnoreCase));
            inv.SerialPorts.Add(new SerialPortInfo
            {
                Name = name,
                FriendlyName = friendlyByName.TryGetValue(name, out var f) ? f : id?.FriendlyName,
                Vid = id?.Vid?.ToString("X4"),
                Pid = id?.Pid?.ToString("X4"),
                Serial = id?.Serial,
                Enumerator = id?.Enumerator
            });
        }

        foreach (var hid in HidSharp.DeviceList.Local.GetHidDevices())
        {
            var d = Readers.HidDeviceReader.DescribeDevice(hid);
            inv.HidDevices.Add(new HidDeviceInfo
            {
                Vid = d["vid"],
                Pid = d["pid"],
                Product = d.GetValueOrDefault("product"),
                Manufacturer = d.GetValueOrDefault("manufacturer"),
                Serial = d.GetValueOrDefault("serial"),
                Usages = d.GetValueOrDefault("usages"),
                IsScale = d.GetValueOrDefault("isScale") == "true",
                DevicePath = d.GetValueOrDefault("devicePath"),
                MaxInputReportLength = int.TryParse(d.GetValueOrDefault("maxInputReportLength"), out var len) ? len : null
            });
        }

        return inv;
    }
#endif
}
