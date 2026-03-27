#if WINDOWS
using System.Drawing;
using System.Drawing.Printing;

namespace CloudPrint.Service.Printing;

public class DocumentPrinter : IDocumentPrinter
{
    private readonly ILogger<DocumentPrinter> _logger;

    public DocumentPrinter(ILogger<DocumentPrinter> logger)
    {
        _logger = logger;
    }

    public void Print(string filePath, string printerName, string contentType)
    {
        ValidatePrinterExists(printerName);

        using var image = Image.FromFile(filePath);
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        doc.DocumentName = Path.GetFileName(filePath);

        var printed = false;
        doc.PrintPage += (sender, e) =>
        {
            if (e.Graphics is null) return;

            var destRect = e.MarginBounds;
            var srcRect = new Rectangle(0, 0, image.Width, image.Height);

            // Scale to fit within margins while preserving aspect ratio
            var scale = Math.Min(
                (float)destRect.Width / image.Width,
                (float)destRect.Height / image.Height);

            var width = (int)(image.Width * scale);
            var height = (int)(image.Height * scale);

            e.Graphics.DrawImage(image,
                new Rectangle(destRect.X, destRect.Y, width, height),
                srcRect, GraphicsUnit.Pixel);

            e.HasMorePages = false;
            printed = true;
        };

        doc.Print();
        _logger.LogDebug("Printed document to {Printer} via PrintDocument API", printerName);

        if (!printed)
            throw new InvalidOperationException("PrintPage event was never raised");
    }

    private static void ValidatePrinterExists(string printerName)
    {
        foreach (string printer in PrinterSettings.InstalledPrinters)
        {
            if (string.Equals(printer, printerName, StringComparison.OrdinalIgnoreCase))
                return;
        }
        throw new PrinterNotFoundException(printerName);
    }
}
#endif
