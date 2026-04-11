namespace CloudPrint.Service.Printing;

public interface IPdfPrinter
{
    void Print(string filePath, string printerName);
}
