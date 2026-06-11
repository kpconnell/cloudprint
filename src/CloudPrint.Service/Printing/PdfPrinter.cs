#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Docnet.Core;
using Docnet.Core.Converters;
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
                // Flatten onto white: PDFium leaves unpainted areas transparent, and
                // print drivers handle alpha unpredictably (dithered/haloed edges).
                var rawBytes = pageReader.GetImage(new NaiveTransparencyRemover()); // BGRA, opaque

                if (settings.Monochrome)
                    ThresholdToBlackAndWhite(rawBytes);

                // Format32bppRgb: same BGRA memory layout, but the driver never
                // sees an alpha channel.
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
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

            var paper = ResolvePaperSize(doc.PrinterSettings, settings.PaperSize, _logger);
            if (paper is not null)
                doc.DefaultPageSettings.PaperSize = paper;

            doc.PrintPage += (_, e) =>
            {
                if (e.Graphics is null) return;
                var page = pages[pageIndex];

                Rectangle dest;
                if (fitToPhysicalPage)
                {
                    // The print Graphics origin sits at the top-left of the printable
                    // area; shift back by the hard margins so the bitmap aligns to the
                    // physical sheet instead of drifting down-right.
                    dest = e.PageBounds;
                    dest.Offset((int)-e.PageSettings.HardMarginX, (int)-e.PageSettings.HardMarginY);
                }
                else
                {
                    dest = e.MarginBounds;
                }

                // When render DPI matches the device this is ~1:1; for any residual
                // scaling, avoid GDI+'s low-quality default resampler.
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

                var scale = Math.Min(
                    (float)dest.Width / page.Width,
                    (float)dest.Height / page.Height);

                if (pageIndex == 0)
                {
                    // One line per job describing what the driver actually gave us —
                    // paper/DPI mismatches are invisible without this.
                    _logger.LogInformation(
                        "Print geometry for {Printer}: paper={Paper} ({Pw}x{Ph}/100\"), driver={DriverDpi}dpi, " +
                        "renderDpi={RenderDpi}, fit={FitMode}, pageBounds={PageBounds}, marginBounds={MarginBounds}, " +
                        "hardMargins=({Hx},{Hy}), bitmap={Bw}x{Bh}px, scale={Scale:0.###}",
                        printerName, e.PageSettings.PaperSize.PaperName,
                        e.PageSettings.PaperSize.Width, e.PageSettings.PaperSize.Height,
                        e.PageSettings.PrinterResolution.X, settings.Dpi, settings.FitMode,
                        e.PageBounds, e.MarginBounds,
                        e.PageSettings.HardMarginX, e.PageSettings.HardMarginY,
                        page.Width, page.Height, scale);
                }

                // Round, don't truncate: when the bitmap exactly matches the page
                // (render DPI == device DPI), float error must not shave a unit off
                // the dest rect and force a full-bitmap resample.
                e.Graphics.DrawImage(page,
                    new Rectangle(dest.X, dest.Y,
                        (int)Math.Round(page.Width * scale), (int)Math.Round(page.Height * scale)),
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

    // Maps the configured stock ("4x6", "2x2", "Letter", "A4", or any "WxH" in
    // inches) to a PaperSize. Prefers the driver's own stock entry with matching
    // dimensions; falls back to a custom size. Null = leave the queue default.
    private static PaperSize? ResolvePaperSize(PrinterSettings printer, string configured, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        int w, h;
        switch (configured.Trim().ToLowerInvariant())
        {
            case "letter": w = 850; h = 1100; break;
            case "a4": w = 827; h = 1169; break;
            default:
                var parts = configured.Trim().ToLowerInvariant().Split('x');
                if (parts.Length == 2
                    && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var wi)
                    && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var hi)
                    && wi > 0 && hi > 0)
                {
                    w = (int)Math.Round(wi * 100);
                    h = (int)Math.Round(hi * 100);
                }
                else
                {
                    logger.LogWarning("Unrecognized PdfPaperSize '{Size}' — using the queue's default paper", configured);
                    return null;
                }
                break;
        }

        foreach (PaperSize ps in printer.PaperSizes)
            if (Math.Abs(ps.Width - w) <= 5 && Math.Abs(ps.Height - h) <= 5)
                return ps;
        return new PaperSize($"CloudPrint {configured}", w, h);
    }

    // Snap every pixel to pure black or white (Rec. 601 luma). Thermal heads are
    // 1-bit; doing the threshold here keeps output deterministic instead of leaving
    // anti-aliased grays to the driver's dither. 63% (not 50%) keeps more of the
    // anti-aliasing edge pixels, giving small glyphs fuller strokes at 203 dpi.
    private static void ThresholdToBlackAndWhite(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var luma = (bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000;
            var v = luma < 160 ? (byte)0 : (byte)255;
            bgra[i] = v;
            bgra[i + 1] = v;
            bgra[i + 2] = v;
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
