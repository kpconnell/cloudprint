using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudPrint.Configurator.Core.Exe;

/// <summary>AWS credentials passed to the service exe's CLI subcommands.</summary>
public sealed record AwsCredentials(string AccessKey, string SecretKey, string Region);

/// <summary>Parameters for a device-output "Hello" test (the test-output subcommand).</summary>
public sealed record OutputTestRequest
{
    public string Transport { get; init; } = "sqs";
    public string? QueueUrl { get; init; }
    public string? WebhookUrl { get; init; }
    public string? HeaderName { get; init; }
    public string? HeaderValue { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public string? Region { get; init; }
    public string? Station { get; init; }
    public string? DeviceName { get; init; }
    public string? Message { get; init; }
}

/// <summary>A HID device discovered by the "list-devices" subcommand.</summary>
public sealed record HidDeviceInfo(int Vid, int Pid, string Product,
    string? Manufacturer = null, string? Serial = null, string? Usages = null, bool IsScale = false);

/// <summary>A COM port discovered by the "list-devices" subcommand, with the USB identity behind it when known.</summary>
public sealed record SerialPortInfo(string Name, string? FriendlyName = null, int? Vid = null, int? Pid = null, string? Serial = null)
{
    public string Describe() =>
        FriendlyName is null && Vid is null ? Name
        : $"{Name}  —  {FriendlyName ?? "serial"}{(Vid is null ? "" : $" (VID={Vid:X4} PID={Pid:X4})")}";
}

/// <summary>Hardware enumerated by the "list-devices" subcommand.</summary>
public sealed record DeviceInventory(
    IReadOnlyList<SerialPortInfo> SerialPorts, IReadOnlyList<HidDeviceInfo> HidDevices);

/// <summary>Thrown when a service exe subcommand exits non-zero.</summary>
public sealed class ServiceExeException : Exception
{
    public ServiceExeException(string message) : base(message) { }
}

/// <summary>
/// Drives the CloudPrint.Service.exe CLI subcommands (verify-creds, create-queue, list-queues,
/// delete-queue, list-devices). Credentials are passed via stdin JSON so they never appear in a
/// process argument list.
/// </summary>
public sealed class ServiceExeClient
{
    private static readonly JsonSerializerOptions StdinOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IProcessRunner _runner;
    private readonly string _exePath;

    public ServiceExeClient(IProcessRunner runner, string exePath)
    {
        _runner = runner;
        _exePath = exePath;
    }

    /// <summary>Verifies credentials and returns the caller's STS ARN.</summary>
    public async Task<string> VerifyCredentialsAsync(AwsCredentials creds, CancellationToken ct = default)
    {
        var result = await Run("verify-creds", new CliInput(creds), ct);
        return result.StdOut.Trim();
    }

    /// <summary>Creates a main queue (+ DLQ) and returns the main queue URL.</summary>
    public async Task<string> CreateQueueAsync(
        AwsCredentials creds, string queueName, IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken ct = default)
    {
        var input = new CliInput(creds) { QueueName = queueName, Tags = tags };
        var result = await Run("create-queue", input, ct);
        return result.StdOut.Trim();
    }

    /// <summary>Lists existing queue URLs whose names start with the given prefix.</summary>
    public async Task<IReadOnlyList<string>> ListQueuesAsync(
        AwsCredentials creds, string prefix, CancellationToken ct = default)
    {
        var input = new CliInput(creds) { QueueName = prefix };
        var result = await Run("list-queues", input, ct);
        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Deletes a queue by URL (idempotent on the service side).</summary>
    public async Task DeleteQueueAsync(AwsCredentials creds, string queueUrl, CancellationToken ct = default)
    {
        var input = new CliInput(creds) { QueueUrl = queueUrl };
        await Run("delete-queue", input, ct);
    }

    /// <summary>Prints a built-in ZPL test label to the named printer (Windows-only subcommand).</summary>
    public async Task TestPrintAsync(string printerName, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { printerName }, StdinOptions);
        var result = await _runner.RunAsync(_exePath, new[] { "test-print" }, json, ct);
        if (result.ExitCode != 0)
            throw new ServiceExeException(Describe("test-print", result));
    }

    /// <summary>Publishes a "Hello" test reading to a device's output (SQS queue or HTTP webhook).</summary>
    public async Task TestOutputAsync(OutputTestRequest request, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(request, StdinOptions);
        var result = await _runner.RunAsync(_exePath, new[] { "test-output" }, json, ct);
        if (result.ExitCode != 0)
            throw new ServiceExeException(Describe("test-output", result));
    }

    /// <summary>Enumerates serial ports and HID devices on this machine (Windows-only subcommand).</summary>
    public async Task<DeviceInventory> ListDevicesAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(_exePath, new[] { "list-devices", "--json" }, stdin: null, ct);
        if (result.ExitCode != 0)
            throw new ServiceExeException(Describe("list-devices", result));
        return result.StdOut.TrimStart().StartsWith('{')
            ? ParseDeviceInventoryJson(result.StdOut)
            : ParseDeviceInventory(result.StdOut); // older service exe without --json
    }

    /// <summary>Parses the JSON of "list-devices --json" (see CloudPrint.Service.Devices.DeviceInventory).</summary>
    internal static DeviceInventory ParseDeviceInventoryJson(string json)
    {
        var serial = new List<SerialPortInfo>();
        var hid = new List<HidDeviceInfo>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("serialPorts", out var ports) && ports.ValueKind == JsonValueKind.Array)
            foreach (var p in ports.EnumerateArray())
            {
                var name = Str(p, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                serial.Add(new SerialPortInfo(name!, Str(p, "friendlyName"), Hex(p, "vid"), Hex(p, "pid"), Str(p, "serial")));
            }

        if (root.TryGetProperty("hidDevices", out var hids) && hids.ValueKind == JsonValueKind.Array)
            foreach (var h in hids.EnumerateArray())
            {
                var vid = Hex(h, "vid");
                var pid = Hex(h, "pid");
                if (vid is null || pid is null) continue;
                var isScale = h.TryGetProperty("isScale", out var sc) && sc.ValueKind == JsonValueKind.True;
                hid.Add(new HidDeviceInfo(vid.Value, pid.Value, Str(h, "product") ?? string.Empty,
                    Str(h, "manufacturer"), Str(h, "serial"), Str(h, "usages"), isScale));
            }

        return new DeviceInventory(serial, hid);

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Hex(JsonElement e, string name) =>
            Str(e, name) is { } s && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private async Task<ProcessResult> Run(string command, CliInput input, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(input, StdinOptions);
        var result = await _runner.RunAsync(_exePath, new[] { command }, json, ct);
        if (result.ExitCode != 0)
            throw new ServiceExeException(Describe(command, result));
        return result;
    }

    private static string Describe(string command, ProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        return $"'{command}' failed (exit {result.ExitCode}): {detail.Trim()}";
    }

    /// <summary>
    /// Parses the text output of "list-devices":
    /// <code>
    /// Serial ports:
    ///   COM3
    /// HID devices:
    ///   VID=0922 PID=8003 Mettler Toledo
    /// </code>
    /// </summary>
    internal static DeviceInventory ParseDeviceInventory(string stdout)
    {
        var serial = new List<SerialPortInfo>();
        var hid = new List<HidDeviceInfo>();
        var section = 0; // 0 = none, 1 = serial, 2 = hid, 3 = detail (ignored)

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("Serial ports", StringComparison.OrdinalIgnoreCase)) { section = 1; continue; }
            if (line.StartsWith("HID devices", StringComparison.OrdinalIgnoreCase)) { section = 2; continue; }
            if (line.StartsWith("Detail", StringComparison.OrdinalIgnoreCase)) { section = 3; continue; }

            switch (section)
            {
                case 1:
                    serial.Add(new SerialPortInfo(line));
                    break;
                case 2:
                    if (TryParseHid(line, out var info))
                        hid.Add(info);
                    break;
            }
        }

        return new DeviceInventory(serial, hid);
    }

    private static bool TryParseHid(string line, out HidDeviceInfo info)
    {
        info = default!;
        // Expected: "VID=0922 PID=8003 Some Product Name"
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        if (!TryParseHexField(parts[0], "VID=", out var vid)) return false;
        if (!TryParseHexField(parts[1], "PID=", out var pid)) return false;

        var product = parts.Length == 3 ? parts[2].Trim() : string.Empty;
        info = new HidDeviceInfo(vid, pid, product);
        return true;
    }

    private static bool TryParseHexField(string token, string prefix, out int value)
    {
        value = 0;
        if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(
            token.AsSpan(prefix.Length),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>Stdin payload shape; matches CloudPrint.Service.CliInput (case-insensitive on the service side).</summary>
    private sealed class CliInput
    {
        public CliInput(AwsCredentials creds)
        {
            AccessKey = creds.AccessKey.Trim();
            SecretKey = creds.SecretKey;
            Region = creds.Region;
        }

        public string AccessKey { get; }
        public string SecretKey { get; }
        public string Region { get; }
        public string? QueueName { get; init; }
        public string? QueueUrl { get; init; }
        public IReadOnlyDictionary<string, string>? Tags { get; init; }
    }
}
