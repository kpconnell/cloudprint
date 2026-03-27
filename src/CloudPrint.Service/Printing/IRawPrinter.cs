namespace CloudPrint.Service.Printing;

public interface IRawPrinter
{
    void PrintRaw(string filePath, string printerName, string documentName);
}
