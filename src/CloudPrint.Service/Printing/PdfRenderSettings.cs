namespace CloudPrint.Service.Printing;

// Monochrome pre-thresholds the rasterized page to pure black/white so thermal
// drivers print deterministic dots instead of dithering anti-aliased grays.
public record PdfRenderSettings(int Dpi, string FitMode, bool Monochrome = false)
{
    public static readonly PdfRenderSettings Default = new(300, "Margins");
}
