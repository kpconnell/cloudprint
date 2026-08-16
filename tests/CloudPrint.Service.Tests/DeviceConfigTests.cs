using CloudPrint.Service.Configuration;
using Microsoft.Extensions.Configuration;

namespace CloudPrint.Service.Tests;

public class DeviceConfigTests
{
    [Fact]
    public void Resolves_defaults()
    {
        var options = new CloudPrintOptions
        {
            Devices = { new DeviceConfig { Name = "s1", Type = "serial-scale", ComPort = "COM3" } }
        };

        var device = options.ResolvedDevices()[0];

        Assert.Equal(9600, device.BaudRate);
        Assert.Equal("None", device.Parity);
        Assert.Equal(8, device.DataBits);
        Assert.Equal(1, device.StopBits);
        Assert.Equal("crlf", device.LineEnding);
        Assert.Equal("ascii", device.Encoding);
        Assert.Equal("stream", device.PollMode);
        Assert.True(device.StableOnly);
        Assert.Equal("mt-sics", device.Protocol);
        Assert.Equal(Environment.MachineName, device.Station);
        Assert.Equal("sqs", device.Output.Transport);
    }

    [Fact]
    public void Applies_per_device_overrides_and_lowercases_type_and_transport()
    {
        var options = new CloudPrintOptions
        {
            DeviceStableOnly = true,
            Station = "global-station",
            Devices =
            {
                new DeviceConfig
                {
                    Name = "s1",
                    Type = "HID-Scale",
                    Station = "dev-station",
                    StableOnly = false,
                    BaudRate = 4800,
                    Output = new DeviceOutputConfig { Transport = "HTTP", WebhookUrl = "https://x" }
                }
            }
        };

        var device = options.ResolvedDevices()[0];

        Assert.Equal("hid-scale", device.Type);
        Assert.Equal("dev-station", device.Station);
        Assert.False(device.StableOnly);
        Assert.Equal(4800, device.BaudRate);
        Assert.Equal("http", device.Output.Transport);
        Assert.Equal("https://x", device.Output.WebhookUrl);
    }

    [Fact]
    public void Skips_devices_without_a_name()
    {
        var options = new CloudPrintOptions
        {
            Devices = { new DeviceConfig { Name = "", Type = "serial-scale" } }
        };

        Assert.Empty(options.ResolvedDevices());
    }

    [Fact]
    public void Station_falls_back_to_machine_name_when_blank()
    {
        var options = new CloudPrintOptions
        {
            Devices = { new DeviceConfig { Name = "s1" } }
        };

        Assert.Equal(Environment.MachineName, options.ResolvedDevices()[0].Station);
    }

    [Fact]
    public void Binds_devices_from_appsettings_json_shape()
    {
        var json = """
        {
          "CloudPrint": {
            "Transport": "sqs",
            "Station": "shipping-pc-01",
            "DeviceStableOnly": true,
            "Devices": [
              {
                "Name": "scale-shipping",
                "Type": "serial-scale",
                "ComPort": "COM3",
                "PollMode": "request",
                "RequestCommand": "S",
                "InitCommands": [ "Z" ],
                "Output": { "Transport": "sqs", "QueueUrl": "https://q/readings" }
              },
              {
                "Name": "scale-counter",
                "Type": "hid-scale",
                "Vid": 2338,
                "Pid": 24613,
                "Output": { "Transport": "http", "WebhookUrl": "https://wms/readings", "HeaderName": "X-Api-Key", "HeaderValue": "k" }
              }
            ]
          }
        }
        """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = config.GetSection(CloudPrintOptions.SectionName).Get<CloudPrintOptions>()!;

        var devices = options.ResolvedDevices();
        Assert.Equal(2, devices.Count);

        var serial = devices[0];
        Assert.Equal("scale-shipping", serial.Name);
        Assert.Equal("serial-scale", serial.Type);
        Assert.Equal("COM3", serial.ComPort);
        Assert.Equal("request", serial.PollMode);
        Assert.Equal("S", serial.RequestCommand);
        Assert.Equal(new[] { "Z" }, serial.InitCommands);
        Assert.Equal("shipping-pc-01", serial.Station);
        Assert.Equal("sqs", serial.Output.Transport);
        Assert.Equal("https://q/readings", serial.Output.QueueUrl);

        var hid = devices[1];
        Assert.Equal("hid-scale", hid.Type);
        Assert.Equal(2338, hid.Vid);
        Assert.Equal(24613, hid.Pid);
        Assert.Equal("http", hid.Output.Transport);
        Assert.Equal("https://wms/readings", hid.Output.WebhookUrl);
        Assert.Equal("X-Api-Key", hid.Output.HeaderName);
    }
}

public class DeviceConfigFramingTests
{
    private static CloudPrint.Service.Configuration.ResolvedDevice Resolve(Action<CloudPrint.Service.Configuration.DeviceConfig> configure,
        Action<CloudPrint.Service.Configuration.CloudPrintOptions>? global = null)
    {
        var d = new CloudPrint.Service.Configuration.DeviceConfig { Name = "d" };
        configure(d);
        var o = new CloudPrint.Service.Configuration.CloudPrintOptions { Devices = { d } };
        global?.Invoke(o);
        return o.ResolvedDevices()[0];
    }

    [Fact]
    public void Defaults_are_line_crlf_ascii_no_heartbeat()
    {
        var r = Resolve(_ => { });
        Assert.Equal(CloudPrint.Service.Devices.Framing.FrameMode.Line, r.Framing.Mode);
        Assert.Equal("\r\n"u8.ToArray(), r.Framing.Terminator);
        Assert.Equal("\r\n", r.EffectiveCommandTerminator);
        Assert.Equal(TimeSpan.FromSeconds(2), r.ReadTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(150), r.Framing.IdleGap);
        Assert.Equal(4096, r.Framing.MaxFrameBytes);
        Assert.Null(r.Heartbeat);
        Assert.Null(r.StaleAfter);
        Assert.False(r.DtrEnable);
        Assert.False(r.IsTcp);
        Assert.True(r.IsSerial);
    }

    [Fact]
    public void Delimited_framing_unescapes_start_and_end()
    {
        var r = Resolve(d => { d.FrameMode = "delimited"; d.FrameStart = "<STX>"; d.FrameEnd = @"\x03"; });
        Assert.Equal(CloudPrint.Service.Devices.Framing.FrameMode.Delimited, r.Framing.Mode);
        Assert.Equal(new byte[] { 0x02 }, r.Framing.Start);
        Assert.Equal(new byte[] { 0x03 }, r.Framing.End);
    }

    [Fact]
    public void Delimited_without_end_falls_back_to_line_ending()
    {
        var r = Resolve(d => { d.FrameMode = "delimited"; d.LineEnding = "cr"; });
        Assert.Equal(new byte[] { 0x0D }, r.Framing.End);
    }

    [Fact]
    public void Idle_mode_and_aliases()
    {
        Assert.Equal(CloudPrint.Service.Devices.Framing.FrameMode.Idle, Resolve(d => d.FrameMode = "idle").Framing.Mode);
        Assert.Equal(CloudPrint.Service.Devices.Framing.FrameMode.Idle, Resolve(d => d.FrameMode = "discovery").Framing.Mode);
        Assert.Equal(CloudPrint.Service.Devices.Framing.FrameMode.Delimited, Resolve(d => d.FrameMode = "STXETX").Framing.Mode);
        Assert.Equal(TimeSpan.FromMilliseconds(40), Resolve(d => { d.FrameMode = "idle"; d.IdleGapMs = 40; }).Framing.IdleGap);
    }

    [Theory]
    [InlineData(null, "cr", "\r")]
    [InlineData("none", "crlf", "")]
    [InlineData("NONE", "crlf", "")]
    [InlineData("<CR>", "crlf", "\r")]
    [InlineData(@"\r\n", "cr", "\r\n")]
    [InlineData(null, "<ETX><CR>", "\x03\r")]
    public void Command_terminator_resolution(string? terminator, string lineEnding, string expected)
    {
        var r = Resolve(d => { d.CommandTerminator = terminator; d.LineEnding = lineEnding; });
        Assert.Equal(expected, r.EffectiveCommandTerminator);
    }

    [Fact]
    public void Tcp_fields_and_timeouts()
    {
        var r = Resolve(d => { d.Type = "tcp-raw"; d.Host = " 10.1.100.100 "; d.Port = 1050; d.ConnectTimeoutMs = 1500; d.ReadTimeoutMs = 750; });
        Assert.True(r.IsTcp);
        Assert.Equal("10.1.100.100", r.Host);
        Assert.Equal(1050, r.Port);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), r.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(750), r.ReadTimeout);
    }

    [Fact]
    public void Heartbeat_and_stale_inherit_globals_and_override()
    {
        var inherited = Resolve(_ => { }, o => { o.DeviceHeartbeatSeconds = 30; o.DeviceStaleAfterSeconds = 90; });
        Assert.Equal(TimeSpan.FromSeconds(30), inherited.Heartbeat);
        Assert.Equal(TimeSpan.FromSeconds(90), inherited.StaleAfter);

        var overridden = Resolve(d => { d.HeartbeatSeconds = 5; d.StaleAfterSeconds = 0; }, o => { o.DeviceHeartbeatSeconds = 30; o.DeviceStaleAfterSeconds = 90; });
        Assert.Equal(TimeSpan.FromSeconds(5), overridden.Heartbeat);
        Assert.Null(overridden.StaleAfter); // explicit 0 = off
    }

    [Fact]
    public void Committed_sample_resolves_every_device()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "appsettings.sample.json");
        if (!File.Exists(path)) return; // test assets are only guaranteed in-repo
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("CloudPrint").GetRawText();
        var options = System.Text.Json.JsonSerializer.Deserialize<CloudPrint.Service.Configuration.CloudPrintOptions>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var devices = options.ResolvedDevices();
        Assert.Equal(5, devices.Count);
        var cubi = devices.Single(d => d.Name == "cubiscan-125");
        Assert.True(cubi.IsTcp);
        Assert.Equal(CloudPrint.Service.Devices.Framing.FrameMode.Delimited, cubi.Framing.Mode);
        Assert.Equal(new byte[] { 0x02 }, cubi.Framing.Start);
        var auto = devices.Single(d => d.Name == "unknown-usb-serial");
        Assert.Equal("\r", auto.EffectiveCommandTerminator);
        Assert.Equal(CloudPrint.Service.Devices.Framing.FrameMode.Idle, auto.Framing.Mode);
        Assert.Equal(TimeSpan.FromSeconds(30), auto.Heartbeat);
        Assert.False(string.IsNullOrEmpty(options.DeviceCommandQueueUrl));
    }
}
