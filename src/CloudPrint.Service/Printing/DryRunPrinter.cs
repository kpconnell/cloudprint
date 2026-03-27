namespace CloudPrint.Service.Printing;

public class DryRunPrinter : IRawPrinter, IDocumentPrinter
{
    private readonly ILogger<DryRunPrinter> _logger;

    public DryRunPrinter(ILogger<DryRunPrinter> logger)
    {
        _logger = logger;
    }

    public void PrintRaw(string filePath, string printerName, string documentName)
    {
        var fileSize = new FileInfo(filePath).Length;
        _logger.LogInformation("[DRY RUN] Would send {Bytes} bytes raw to printer '{Printer}' (doc: {Document})",
            fileSize, printerName, documentName);
    }

    public void Print(string filePath, string printerName, string contentType)
    {
        var fileSize = new FileInfo(filePath).Length;
        _logger.LogInformation("[DRY RUN] Would print {ContentType} ({Bytes} bytes) to printer '{Printer}'",
            contentType, fileSize, printerName);
    }
}
