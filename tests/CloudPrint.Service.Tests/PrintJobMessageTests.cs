using System.Text.Json;
using CloudPrint.Service.Sqs;

namespace CloudPrint.Service.Tests;

public class PrintJobMessageTests
{
    [Fact]
    public void Deserializes_full_message()
    {
        var json = """
        {
            "fileUrl": "https://s3.amazonaws.com/bucket/label.zpl",
            "printerName": "Zebra_ZP500",
            "contentType": "application/vnd.zebra.zpl",
            "copies": 3,
            "metadata": { "orderId": "12345" }
        }
        """;

        var msg = JsonSerializer.Deserialize<PrintJobMessage>(json)!;

        Assert.Equal("https://s3.amazonaws.com/bucket/label.zpl", msg.FileUrl);
        Assert.Equal("Zebra_ZP500", msg.PrinterName);
        Assert.Equal("application/vnd.zebra.zpl", msg.ContentType);
        Assert.Equal(3, msg.Copies);
        Assert.NotNull(msg.Metadata);
        Assert.Equal("12345", msg.Metadata!["orderId"]);
    }

    [Fact]
    public void Deserializes_minimal_message_with_defaults()
    {
        var json = """
        {
            "fileUrl": "https://example.com/file.pdf",
            "printerName": "HP_LaserJet",
            "contentType": "application/pdf"
        }
        """;

        var msg = JsonSerializer.Deserialize<PrintJobMessage>(json)!;

        Assert.Equal(1, msg.Copies);
        Assert.Null(msg.Metadata);
    }

    [Fact]
    public void Roundtrip_serialization()
    {
        var original = new PrintJobMessage
        {
            FileUrl = "https://example.com/test.zpl",
            PrinterName = "TestPrinter",
            ContentType = "application/vnd.zebra.zpl",
            Copies = 2,
            Metadata = new Dictionary<string, string> { ["key"] = "value" }
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<PrintJobMessage>(json)!;

        Assert.Equal(original.FileUrl, deserialized.FileUrl);
        Assert.Equal(original.PrinterName, deserialized.PrinterName);
        Assert.Equal(original.ContentType, deserialized.ContentType);
        Assert.Equal(original.Copies, deserialized.Copies);
        Assert.Equal(original.Metadata!["key"], deserialized.Metadata!["key"]);
    }
}
