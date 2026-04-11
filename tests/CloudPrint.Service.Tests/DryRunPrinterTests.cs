using CloudPrint.Service.Printing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudPrint.Service.Tests;

public class DryRunPrinterTests
{
    private readonly DryRunPrinter _printer = new(NullLogger<DryRunPrinter>.Instance);

    [Fact]
    public void PrintRaw_does_not_throw()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "^XA^FO50,50^ADN,36,20^FDHello^FS^XZ");
            _printer.PrintRaw(tempFile, "FakePrinter", "test-doc");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Print_does_not_throw()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            _printer.Print(tempFile, "FakePrinter", "image/png");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void DryRunPrinter_implements_IPdfPrinter()
    {
        Assert.IsAssignableFrom<IPdfPrinter>(_printer);
    }

    [Fact]
    public void PrintPdf_does_not_throw()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }); // %PDF-1
            ((IPdfPrinter)_printer).Print(tempFile, "FakePrinter");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
