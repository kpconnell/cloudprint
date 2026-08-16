using System.Text.Json;
using CloudPrint.Configurator.Core.Config;

namespace CloudPrint.Configurator.Core.Tests;

/// <summary>
/// Guards the configurator's config model against drift from the service schema by round-tripping
/// the committed sample configs. If the service adds a field to a sample that the model doesn't
/// capture, <see cref="Model_captures_every_field_in_sample"/> fails with the dropped path.
/// </summary>
public class SampleDriftTests
{
    private static string SamplePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "samples", name);

    [Fact]
    public void Sqs_sample_parses_printers_and_all_device_kinds()
    {
        var config = ConfigStore.Parse(File.ReadAllText(SamplePath("appsettings.sample.json")));
        Assert.NotNull(config);
        Assert.Equal("sqs", config!.Transport);
        Assert.Single(config.Printers);
        Assert.Equal(5, config.Devices.Count);
        Assert.Equal("https://sqs.us-east-1.amazonaws.com/123456789012/cloudprint-shipping-pc-01-device-commands", config.DeviceCommandQueueUrl);

        var serial = config.Devices[0];
        Assert.Equal("serial-scale", serial.Type);
        Assert.Equal("COM3", serial.ComPort);
        Assert.Equal(9600, serial.BaudRate);
        Assert.Equal(new[] { "Z" }, serial.InitCommands);
        Assert.Equal("sqs", serial.Output!.Transport);

        var hid = config.Devices[1];
        Assert.Equal("hid-scale", hid.Type);
        Assert.Equal(2338, hid.Vid);
        Assert.Equal(24613, hid.Pid);
        Assert.Equal("http", hid.Output!.Transport);
        Assert.Equal("https://wms.example.com/api/readings", hid.Output.WebhookUrl);

        var raw = config.Devices[2];
        Assert.Equal("serial-raw", raw.Type);
        Assert.False(string.IsNullOrWhiteSpace(raw.Pattern));

        var tcp = config.Devices[3];
        Assert.Equal("tcp-raw", tcp.Type);
        Assert.Equal("10.1.100.100", tcp.Host);
        Assert.Equal(1050, tcp.Port);
        Assert.Equal("delimited", tcp.FrameMode);
        Assert.Equal("<STX>", tcp.FrameStart);
        Assert.Equal("<ETX>", tcp.FrameEnd);
        Assert.Equal(new[] { "<STX>T<ETX>" }, tcp.InitCommands);

        var auto = config.Devices[4];
        Assert.Equal("auto", auto.ComPort);
        Assert.Equal("idle", auto.FrameMode);
        Assert.Equal("<CR>", auto.CommandTerminator);
        Assert.Equal(30, auto.HeartbeatSeconds);
    }

    [Fact]
    public void Http_sample_parses_transport_and_device()
    {
        var config = ConfigStore.Parse(File.ReadAllText(SamplePath("appsettings.http-transport.sample.json")));
        Assert.NotNull(config);
        Assert.Equal("http", config!.Transport);
        Assert.Equal("Zebra_ZP500", config.PrinterName);
        Assert.Single(config.Devices);
    }

    [Theory]
    [InlineData("appsettings.sample.json")]
    [InlineData("appsettings.http-transport.sample.json")]
    public void Model_captures_every_field_in_sample(string sample)
    {
        var original = File.ReadAllText(SamplePath(sample));
        using var origDoc = JsonDocument.Parse(original);
        var origSection = origDoc.RootElement.GetProperty("CloudPrint");

        var roundTrip = ConfigStore.BuildJson(ConfigStore.Parse(original)!);
        using var rtDoc = JsonDocument.Parse(roundTrip);
        var rtSection = rtDoc.RootElement.GetProperty("CloudPrint");

        var origPaths = new HashSet<string>();
        Collect(origSection, "", origPaths);
        var rtPaths = new HashSet<string>();
        Collect(rtSection, "", rtPaths);

        var missing = origPaths.Where(p => !rtPaths.Contains(p)).OrderBy(p => p).ToList();
        Assert.True(missing.Count == 0,
            $"Configurator model dropped fields present in {sample}: {string.Join(", ", missing)}");
    }

    // Collects dotted property paths (array indices normalized to "[]") for every object key in the tree.
    private static void Collect(JsonElement el, string path, HashSet<string> paths)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Name.StartsWith('_'))
                        continue; // skip _README-style documentation keys
                    var child = path.Length == 0 ? prop.Name : $"{path}.{prop.Name}";
                    paths.Add(child);
                    Collect(prop.Value, child, paths);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    Collect(item, $"{path}[]", paths);
                break;
        }
    }
}
