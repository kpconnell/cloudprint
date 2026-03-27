#if WINDOWS
using System.Runtime.InteropServices;

namespace CloudPrint.Service.Printing;

public class RawPrinter : IRawPrinter
{
    private readonly ILogger<RawPrinter> _logger;

    public RawPrinter(ILogger<RawPrinter> logger)
    {
        _logger = logger;
    }

    public void PrintRaw(string filePath, string printerName, string documentName)
    {
        var bytes = File.ReadAllBytes(filePath);

        if (!NativeMethods.OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw new PrinterNotFoundException(printerName);
        }

        try
        {
            var docInfo = new NativeMethods.DOC_INFO_1
            {
                pDocName = documentName,
                pOutputFile = null,
                pDataType = "RAW"
            };

            if (NativeMethods.StartDocPrinter(hPrinter, 1, ref docInfo) == 0)
                throw new InvalidOperationException($"StartDocPrinter failed: {Marshal.GetLastWin32Error()}");

            try
            {
                if (!NativeMethods.StartPagePrinter(hPrinter))
                    throw new InvalidOperationException($"StartPagePrinter failed: {Marshal.GetLastWin32Error()}");

                try
                {
                    var unmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);
                        if (!NativeMethods.WritePrinter(hPrinter, unmanagedBytes, bytes.Length, out var written))
                            throw new InvalidOperationException($"WritePrinter failed: {Marshal.GetLastWin32Error()}");

                        _logger.LogDebug("Sent {Bytes} bytes to printer {Printer}", written, printerName);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(unmanagedBytes);
                    }
                }
                finally
                {
                    NativeMethods.EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                NativeMethods.EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            NativeMethods.ClosePrinter(hPrinter);
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DOC_INFO_1
        {
            public string? pDocName;
            public string? pOutputFile;
            public string? pDataType;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool OpenPrinter(string pPrinterName, out IntPtr hPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 pDocInfo);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);
    }
}
#endif
