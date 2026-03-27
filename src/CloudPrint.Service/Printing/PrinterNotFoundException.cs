namespace CloudPrint.Service.Printing;

public class PrinterNotFoundException : Exception
{
    public string PrinterName { get; }

    public PrinterNotFoundException(string printerName)
        : base($"Printer '{printerName}' was not found on this machine")
    {
        PrinterName = printerName;
    }
}
