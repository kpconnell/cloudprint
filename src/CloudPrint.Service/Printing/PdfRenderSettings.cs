namespace CloudPrint.Service.Printing;

public record PdfRenderSettings(int Dpi, string FitMode)
{
    public static readonly PdfRenderSettings Default = new(300, "Margins");
}
