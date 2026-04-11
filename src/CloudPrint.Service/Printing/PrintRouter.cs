namespace CloudPrint.Service.Printing;

public class PrintRouter
{
    private readonly IRawPrinter _rawPrinter;
    private readonly IDocumentPrinter _documentPrinter;
    private readonly IPdfPrinter _pdfPrinter;
    private readonly ILogger<PrintRouter> _logger;

    private static readonly HashSet<string> RawContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.zebra.zpl",
        "text/plain"
    };

    private static readonly HashSet<string> ImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/bmp",
        "image/gif",
        "image/tiff"
    };

    private static readonly HashSet<string> PdfContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf"
    };

    public PrintRouter(IRawPrinter rawPrinter, IDocumentPrinter documentPrinter, IPdfPrinter pdfPrinter, ILogger<PrintRouter> logger)
    {
        _rawPrinter = rawPrinter;
        _documentPrinter = documentPrinter;
        _pdfPrinter = pdfPrinter;
        _logger = logger;
    }

    public void Print(string filePath, string printerName, string contentType)
    {
        if (RawContentTypes.Contains(contentType))
        {
            _logger.LogDebug("Routing {ContentType} to raw printer", contentType);
            _rawPrinter.PrintRaw(filePath, printerName, $"CloudPrint-{Path.GetFileName(filePath)}");
        }
        else if (ImageContentTypes.Contains(contentType))
        {
            _logger.LogDebug("Routing {ContentType} to document printer", contentType);
            _documentPrinter.Print(filePath, printerName, contentType);
        }
        else if (PdfContentTypes.Contains(contentType))
        {
            _logger.LogDebug("Routing {ContentType} to PDF printer", contentType);
            _pdfPrinter.Print(filePath, printerName);
        }
        else
        {
            throw new NotSupportedException($"Content type '{contentType}' is not supported");
        }
    }
}
