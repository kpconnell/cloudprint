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
}
