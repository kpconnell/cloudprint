using CloudPrint.Service.Configuration;
using Microsoft.Extensions.Configuration;

namespace CloudPrint.Service.Tests;

public class CloudPrintOptionsTests
{
    [Fact]
    public void Resolved_lanes_returns_explicit_printers_when_present()
    {
        var options = new CloudPrintOptions
        {
            PdfRenderDpi = 300,
            PdfFitMode = "Margins",
            Printers =
            {
                new PrinterLane { PrinterName = "Zebra", QueueUrl = "https://q1" },
                new PrinterLane { PrinterName = "HP",    QueueUrl = "https://q2" }
            }
        };

        var lanes = options.ResolvedSqsLanes();

        Assert.Equal(2, lanes.Count);
        Assert.Equal("Zebra", lanes[0].PrinterName);
        Assert.Equal("https://q1", lanes[0].QueueUrl);
        Assert.Equal("HP", lanes[1].PrinterName);
        Assert.Equal("https://q2", lanes[1].QueueUrl);
    }

    [Fact]
    public void Lane_pdf_fields_inherit_global_defaults_when_unset()
    {
        var options = new CloudPrintOptions
        {
            PdfRenderDpi = 300,
            PdfFitMode = "Margins",
            Printers = { new PrinterLane { PrinterName = "Zebra", QueueUrl = "https://q" } }
        };

        var lane = options.ResolvedSqsLanes()[0];

        Assert.Equal(300, lane.PdfRenderDpi);
        Assert.Equal("Margins", lane.PdfFitMode);
    }

    [Fact]
    public void Lane_pdf_fields_override_global_defaults_when_set()
    {
        var options = new CloudPrintOptions
        {
            PdfRenderDpi = 300,
            PdfFitMode = "Margins",
            Printers =
            {
                new PrinterLane
                {
                    PrinterName = "Zebra",
                    QueueUrl = "https://q",
                    PdfRenderDpi = 203,
                    PdfFitMode = "PhysicalPage"
                }
            }
        };

        var lane = options.ResolvedSqsLanes()[0];

        Assert.Equal(203, lane.PdfRenderDpi);
        Assert.Equal("PhysicalPage", lane.PdfFitMode);
    }

    [Fact]
    public void Lane_pdf_fields_partial_override_falls_back_to_global_for_missing()
    {
        var options = new CloudPrintOptions
        {
            PdfRenderDpi = 300,
            PdfFitMode = "Margins",
            Printers =
            {
                new PrinterLane
                {
                    PrinterName = "Zebra",
                    QueueUrl = "https://q",
                    PdfRenderDpi = 203
                    // PdfFitMode unset → should inherit "Margins"
                }
            }
        };

        var lane = options.ResolvedSqsLanes()[0];

        Assert.Equal(203, lane.PdfRenderDpi);
        Assert.Equal("Margins", lane.PdfFitMode);
    }

    [Fact]
    public void Legacy_single_printer_config_promotes_to_one_lane()
    {
        var options = new CloudPrintOptions
        {
            PrinterName = "Zebra_ZP500",
            QueueUrl = "https://legacy.queue/url",
            PdfRenderDpi = 203,
            PdfFitMode = "PhysicalPage"
        };

        var lanes = options.ResolvedSqsLanes();

        Assert.Single(lanes);
        Assert.Equal("Zebra_ZP500", lanes[0].PrinterName);
        Assert.Equal("https://legacy.queue/url", lanes[0].QueueUrl);
        Assert.Equal(203, lanes[0].PdfRenderDpi);
        Assert.Equal("PhysicalPage", lanes[0].PdfFitMode);
    }

    [Fact]
    public void Empty_printers_and_no_legacy_returns_empty()
    {
        var options = new CloudPrintOptions();

        var lanes = options.ResolvedSqsLanes();

        Assert.Empty(lanes);
    }

    [Fact]
    public void Legacy_promotion_ignored_when_printers_array_is_set()
    {
        // Both shapes present: explicit Printers[] wins; the legacy fields are not promoted.
        var options = new CloudPrintOptions
        {
            PrinterName = "LegacyPrinter",
            QueueUrl = "https://legacy",
            Printers = { new PrinterLane { PrinterName = "Modern", QueueUrl = "https://modern" } }
        };

        var lanes = options.ResolvedSqsLanes();

        Assert.Single(lanes);
        Assert.Equal("Modern", lanes[0].PrinterName);
    }

    [Fact]
    public void Legacy_partial_config_does_not_promote()
    {
        // PrinterName set but QueueUrl missing — incomplete legacy shape.
        var options = new CloudPrintOptions
        {
            PrinterName = "OnlyName"
        };

        Assert.Empty(options.ResolvedSqsLanes());
    }

    [Fact]
    public void Lane_with_zero_pdf_dpi_falls_back_to_global()
    {
        // Configuration binding may produce 0 if a JSON value is malformed/missing — treat 0 as unset.
        var options = new CloudPrintOptions
        {
            PdfRenderDpi = 300,
            Printers =
            {
                new PrinterLane { PrinterName = "Zebra", QueueUrl = "https://q", PdfRenderDpi = 0 }
            }
        };

        Assert.Equal(300, options.ResolvedSqsLanes()[0].PdfRenderDpi);
    }

    [Fact]
    public void Lane_with_whitespace_fit_mode_falls_back_to_global()
    {
        var options = new CloudPrintOptions
        {
            PdfFitMode = "Margins",
            Printers =
            {
                new PrinterLane { PrinterName = "Zebra", QueueUrl = "https://q", PdfFitMode = "   " }
            }
        };

        Assert.Equal("Margins", options.ResolvedSqsLanes()[0].PdfFitMode);
    }

    [Fact]
    public void Binds_multi_printer_config_from_appsettings_json_shape()
    {
        // End-to-end: round-trip through Microsoft.Extensions.Configuration to mirror the
        // exact binding the service does at startup.
        var json = """
        {
          "CloudPrint": {
            "Transport": "sqs",
            "Region": "us-east-2",
            "AwsAccessKeyId": "AKIA...",
            "AwsSecretAccessKey": "...",
            "PdfRenderDpi": 300,
            "PdfFitMode": "Margins",
            "Printers": [
              { "PrinterName": "Zebra_ZP500", "QueueUrl": "https://q1", "PdfRenderDpi": 203, "PdfFitMode": "PhysicalPage" },
              { "PrinterName": "HP_LaserJet", "QueueUrl": "https://q2" }
            ]
          }
        }
        """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = config.GetSection(CloudPrintOptions.SectionName).Get<CloudPrintOptions>()!;

        Assert.Equal("sqs", options.Transport);
        Assert.Equal(2, options.Printers.Count);

        var lanes = options.ResolvedSqsLanes();
        Assert.Equal(2, lanes.Count);

        Assert.Equal("Zebra_ZP500", lanes[0].PrinterName);
        Assert.Equal(203, lanes[0].PdfRenderDpi);
        Assert.Equal("PhysicalPage", lanes[0].PdfFitMode);

        Assert.Equal("HP_LaserJet", lanes[1].PrinterName);
        Assert.Equal(300, lanes[1].PdfRenderDpi);    // inherited from global
        Assert.Equal("Margins", lanes[1].PdfFitMode); // inherited from global
    }

    [Fact]
    public void Binds_legacy_single_printer_config_from_appsettings_json_shape()
    {
        var json = """
        {
          "CloudPrint": {
            "Transport": "sqs",
            "Region": "us-east-2",
            "PrinterName": "LegacyZebra",
            "QueueUrl": "https://legacy.queue/url",
            "PdfRenderDpi": 203,
            "PdfFitMode": "PhysicalPage"
          }
        }
        """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = config.GetSection(CloudPrintOptions.SectionName).Get<CloudPrintOptions>()!;

        var lanes = options.ResolvedSqsLanes();
        Assert.Single(lanes);
        Assert.Equal("LegacyZebra", lanes[0].PrinterName);
        Assert.Equal("https://legacy.queue/url", lanes[0].QueueUrl);
        Assert.Equal(203, lanes[0].PdfRenderDpi);
        Assert.Equal("PhysicalPage", lanes[0].PdfFitMode);
    }
}
