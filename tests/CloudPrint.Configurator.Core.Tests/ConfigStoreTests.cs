using CloudPrint.Configurator.Core.Config;

namespace CloudPrint.Configurator.Core.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void BuildJson_default_uses_information_level()
    {
        var json = ConfigStore.BuildJson(new CloudPrintConfig { DumpPayloads = false });
        Assert.Contains("\"Serilog\"", json);
        Assert.Contains("\"Default\": \"Information\"", json);
        Assert.DoesNotContain("\"Debug\"", json);
    }

    [Fact]
    public void BuildJson_dump_uses_debug_level()
    {
        var json = ConfigStore.BuildJson(new CloudPrintConfig { DumpPayloads = true });
        Assert.Contains("\"Default\": \"Debug\"", json);
    }

    [Fact]
    public void RoundTrips_sqs_config_with_printers_and_devices()
    {
        var config = new CloudPrintConfig
        {
            Transport = "sqs",
            Region = "us-east-2",
            AwsAccessKeyId = "AKIATEST",
            AwsSecretAccessKey = "secret+with/chars",
            VisibilityTimeoutSeconds = 300,
            PdfRenderDpi = 300,
            PdfFitMode = "Margins",
            Station = "pc-1",
            DevicePollIntervalMs = 500,
            DeviceStableOnly = true,
            Printers =
            {
                new PrinterLaneModel
                {
                    PrinterName = "Zebra",
                    QueueUrl = "https://q/1",
                    PdfRenderDpi = 203,
                    PdfFitMode = "PhysicalPage",
                    PdfMonochrome = true,
                },
            },
            Devices =
            {
                new DeviceModel
                {
                    Name = "scale",
                    Type = "serial-scale",
                    ComPort = "COM3",
                    BaudRate = 9600,
                    InitCommands = new() { "Z" },
                    Output = new DeviceOutputModel { Transport = "sqs", QueueUrl = "https://q/2" },
                },
            },
        };

        var parsed = ConfigStore.Parse(ConfigStore.BuildJson(config))!;

        Assert.Equal("us-east-2", parsed.Region);
        Assert.Equal("AKIATEST", parsed.AwsAccessKeyId);
        Assert.Equal("secret+with/chars", parsed.AwsSecretAccessKey);
        var lane = Assert.Single(parsed.Printers);
        Assert.Equal(203, lane.PdfRenderDpi);
        Assert.True(lane.PdfMonochrome);
        var device = Assert.Single(parsed.Devices);
        Assert.Equal("COM3", device.ComPort);
        Assert.Equal(new[] { "Z" }, device.InitCommands);
        Assert.Equal("https://q/2", device.Output!.QueueUrl);
    }

    [Fact]
    public void Relaxed_encoder_keeps_plus_and_slash_literal()
    {
        // AWS secrets routinely contain '+' and '/'; they must not become \uXXXX escapes.
        var json = ConfigStore.BuildJson(new CloudPrintConfig { AwsSecretAccessKey = "a+b/c=" });
        Assert.Contains("a+b/c=", json);
        Assert.DoesNotContain("\\u002B", json);
    }

    [Fact]
    public void Parse_returns_null_when_no_cloudprint_section()
    {
        Assert.Null(ConfigStore.Parse("{ \"Other\": {} }"));
    }

    [Fact]
    public void Save_then_Load_roundtrips_through_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cp-config-{Guid.NewGuid():N}", "appsettings.json");
        try
        {
            ConfigStore.Save(path, new CloudPrintConfig { Transport = "http", PrinterName = "P1" });
            var loaded = ConfigStore.Load(path)!;
            Assert.Equal("http", loaded.Transport);
            Assert.Equal("P1", loaded.PrinterName);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
