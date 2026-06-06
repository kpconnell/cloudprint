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
