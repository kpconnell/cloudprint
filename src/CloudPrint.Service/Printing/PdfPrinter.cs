#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Docnet.Core;
using Docnet.Core.Models;

namespace CloudPrint.Service.Printing;

[SupportedOSPlatform("windows")]
public class PdfPrinter : IPdfPrinter
{
    private readonly ILogger<PdfPrinter> _logger;

    // PDFium is not thread-safe across concurrent calls. With multiple printer lanes
    // running in parallel hosted services, two PDFs could rasterize at the same time
    // and corrupt internal Docnet/Pdfium state. Serialize the rasterization stage
    // (the print pipeline itself can run in parallel once bitmaps are produced).
    private static readonly object _pdfiumLock = new();

    public PdfPrinter(ILogger<PdfPrinter> logger)
    {
        _logger = logger;
    }

    public void Print(string filePath, string printerName, PdfRenderSettings settings)
    {
        ValidatePrinterExists(printerName);

        // Scale factor from PDF's native 72 DPI to the configured render DPI
        var dpi = settings.Dpi > 0 ? settings.Dpi : 300;
        var scaleFactor = dpi / 72.0;
        var fitToPhysicalPage = string.Equals(
            settings.FitMode, "PhysicalPage", StringComparison.OrdinalIgnoreCase);

        var pages = new List<Bitmap>();
        int pageCount;

        // Rasterize under the lock; release before driving the print pipeline.
        lock (_pdfiumLock)
        {
            // DocLib.Instance is a process-wide singleton — do NOT dispose it
            using var pdfDoc = DocLib.Instance.GetDocReader(filePath, new PageDimensions(scaleFactor));
            pageCount = pdfDoc.GetPageCount();
            _logger.LogDebug("Rasterizing PDF {File} ({Pages} pages) for {Printer}",
                Path.GetFileName(filePath), pageCount, printerName);

            for (var i = 0; i < pageCount; i++)
            {
                using var pageReader = pdfDoc.GetPageReader(i);
                var w = pageReader.GetPageWidth();
                var h = pageReader.GetPageHeight();
                var rawBytes = pageReader.GetImage(); // BGRA

                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try { Marshal.Copy(rawBytes, 0, data.Scan0, rawBytes.Length); }
                finally { bmp.UnlockBits(data); }
                pages.Add(bmp);
            }
        }

        try
        {
            var pageIndex = 0;
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerName;
            doc.DocumentName = Path.GetFileName(filePath);

            doc.PrintPage += (_, e) =>
            {
                if (e.Graphics is null) return;
                var page = pages[pageIndex];
                var dest = fitToPhysicalPage ? e.PageBounds : e.MarginBounds;
                var scale = Math.Min(
                    (float)dest.Width / page.Width,
                    (float)dest.Height / page.Height);
                e.Graphics.DrawImage(page,
                    new Rectangle(dest.X, dest.Y, (int)(page.Width * scale), (int)(page.Height * scale)),
                    new Rectangle(0, 0, page.Width, page.Height),
                    GraphicsUnit.Pixel);
                pageIndex++;
                e.HasMorePages = pageIndex < pageCount;
            };

            doc.Print();
            _logger.LogDebug("Printed {Pages}-page PDF to {Printer}", pageCount, printerName);
        }
        finally
        {
            foreach (var bmp in pages) bmp.Dispose();
        }
    }

    private static void ValidatePrinterExists(string printerName)
    {
        foreach (string p in PrinterSettings.InstalledPrinters)
            if (string.Equals(p, printerName, StringComparison.OrdinalIgnoreCase)) return;
        throw new PrinterNotFoundException(printerName);
    }
}
#endif
