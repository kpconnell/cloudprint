namespace CloudPrint.Service.Printing;

public interface IDocumentPrinter
{
    void Print(string filePath, string printerName, string contentType);
}
