#if WINDOWS
using System.IO.Ports;
using System.Text.RegularExpressions;
using CloudPrint.Service.Configuration;
using Microsoft.Win32;

namespace CloudPrint.Service.Devices.Channels;

/// <summary>
/// Maps between COM port names and the USB identity behind them (VID/PID/serial, friendly name) using the
/// PnP enumeration tree in the registry (HKLM\SYSTEM\CurrentControlSet\Enum\{USB,FTDIBUS,...}\...\Device
/// Parameters\PortName). Lets a device be configured by VID/PID instead of a COM number that changes when
/// the adapter moves to another USB port, and gives list-devices something better than bare "COM3".
/// Windows-only; every registry read is best-effort (ACLs on some Enum keys are restrictive for non-admins).
/// </summary>
public static class SerialPortLocator
{
    public sealed record PortIdentity(string PortName, int? Vid, int? Pid, string? Serial, string? Enumerator, string? FriendlyName, string? InstanceId);

    private static readonly Regex VidPid = new(@"VID_([0-9A-Fa-f]{4})[&+]PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);
    private static readonly HashSet<string> Enumerators = new(StringComparer.OrdinalIgnoreCase)
        { "USB", "FTDIBUS", "SILABSER", "ACPI", "ROOT", "BTHENUM" };

    /// <summary>Resolves the COM port to open: the configured name, or by VID/PID (+ optional serial in ComPort "auto:SERIAL").</summary>
    public static string Resolve(ResolvedDevice device)
    {
        var configured = device.ComPort?.Trim();
        var wantsAuto = string.IsNullOrEmpty(configured) || configured.StartsWith("auto", StringComparison.OrdinalIgnoreCase);
        if (!wantsAuto)
            return configured!;

        if (device.Vid is null || device.Pid is null)
            throw new ArgumentException($"Device '{device.Name}' has no ComPort configured and no Vid/Pid to find one by");

        string? wantedSerial = null;
        if (configured is { Length: > 5 } && configured.Contains(':'))
            wantedSerial = configured[(configured.IndexOf(':') + 1)..].Trim();

        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var match = Enumerate()
            .Where(p => p.Vid == device.Vid && p.Pid == device.Pid && present.Contains(p.PortName))
            .Where(p => wantedSerial is null || string.Equals(p.Serial, wantedSerial, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return match?.PortName
            ?? throw new IOException($"No COM port present for VID={device.Vid:X4} PID={device.Pid:X4}" +
                                     (wantedSerial is null ? "" : $" serial={wantedSerial}"));
    }

    /// <summary>Discovery facts about a port for the connected event / list-devices.</summary>
    public static IReadOnlyDictionary<string, string> Describe(string portName)
    {
        var d = new Dictionary<string, string>();
        try
        {
            var id = Enumerate().FirstOrDefault(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase));
            if (id is not null)
            {
                if (id.Vid is { } v) d["vid"] = v.ToString("X4");
                if (id.Pid is { } p) d["pid"] = p.ToString("X4");
                if (!string.IsNullOrEmpty(id.Serial)) d["serial"] = id.Serial;
                if (!string.IsNullOrEmpty(id.Enumerator)) d["enumerator"] = id.Enumerator;
                if (!string.IsNullOrEmpty(id.FriendlyName)) d["friendlyName"] = id.FriendlyName;
                if (!string.IsNullOrEmpty(id.InstanceId)) d["instanceId"] = id.InstanceId;
            }
        }
        catch { /* best effort */ }
        return d;
    }

    /// <summary>All COM ports known to PnP with whatever identity the registry exposes (present or not).</summary>
    public static IReadOnlyList<PortIdentity> Enumerate()
    {
        var result = new List<PortIdentity>();
        try
        {
            using var enumRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            if (enumRoot is null) return result;

            // Only walk enumerators that host COM ports; the full Enum tree is thousands of keys.
            foreach (var enumerator in enumRoot.GetSubKeyNames().Where(e => Enumerators.Contains(e)))
            {
                RegistryKey? enumKey = null;
                try { enumKey = enumRoot.OpenSubKey(enumerator); } catch { }
                if (enumKey is null) continue;
                using (enumKey)
                {
                    foreach (var deviceId in SafeSubKeys(enumKey))
                    {
                        RegistryKey? devKey = null;
                        try { devKey = enumKey.OpenSubKey(deviceId); } catch { }
                        if (devKey is null) continue;
                        using (devKey)
                        {
                            foreach (var instance in SafeSubKeys(devKey))
                            {
                                RegistryKey? instKey = null;
                                try { instKey = devKey.OpenSubKey(instance); } catch { }
                                if (instKey is null) continue;
                                using (instKey)
                                {
                                    string? portName = null;
                                    try
                                    {
                                        using var parms = instKey.OpenSubKey("Device Parameters");
                                        portName = parms?.GetValue("PortName") as string;
                                    }
                                    catch { }
                                    if (string.IsNullOrEmpty(portName) || !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                                        continue;

                                    var m = VidPid.Match(deviceId);
                                    int? vid = m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : null;
                                    int? pid = m.Success ? Convert.ToInt32(m.Groups[2].Value, 16) : null;
                                    string? friendly = null;
                                    try { friendly = instKey.GetValue("FriendlyName") as string ?? instKey.GetValue("DeviceDesc") as string; } catch { }
                                    if (friendly is not null && friendly.Contains(';')) friendly = friendly[(friendly.LastIndexOf(';') + 1)..];

                                    // FTDIBUS instance ids look like "VID_0403+PID_6001+A50285BIA\0000"; USB ones are the serial (or a location id like 5&2b3c&0&2)
                                    string? serial = enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) && !instance.Contains('&') ? instance
                                        : enumerator.Equals("FTDIBUS", StringComparison.OrdinalIgnoreCase) && deviceId.Count(c => c == '+') >= 2 ? FtdiSerial(deviceId)
                                        : null;

                                    result.Add(new PortIdentity(portName, vid, pid, serial, enumerator, friendly, $@"{enumerator}\{deviceId}\{instance}"));
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { /* registry unavailable: return what we have */ }
        return result;
    }

    // FTDIBUS device ids look like "VID_0403+PID_6001+A50285BIA": serial "A50285BI" + interface letter "A".
    private static string FtdiSerial(string deviceId)
    {
        var tail = deviceId[(deviceId.LastIndexOf('+') + 1)..];
        return tail.Length > 1 && char.IsLetter(tail[^1]) ? tail[..^1] : tail;
    }

    private static string[] SafeSubKeys(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); } catch { return Array.Empty<string>(); }
    }
}
#endif
