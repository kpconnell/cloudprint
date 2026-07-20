namespace CloudPrint.Service.Printing;

// Monochrome pre-thresholds the rasterized page to pure black/white so thermal
// drivers print deterministic dots instead of dithering anti-aliased grays.
// PaperSize selects the stock loaded in the printer ("4x6", "2x2", "Letter",
// "A4", or any "WxH" in inches); empty = the queue's driver default.
public record PdfRenderSettings(int Dpi, string FitMode, bool Monochrome = false, string PaperSize = "")
{
    public static readonly PdfRenderSettings Default = new(300, "PhysicalPage");
}
