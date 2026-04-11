using CloudPrint.Service.FileHandling;

namespace CloudPrint.Service.Tests;

public class FileValidatorTests
{
    [Fact]
    public void Validates_zpl_starting_with_caret_XA()
    {
        var path = WriteTempFile("^XA^FO50,50^ADN,36,20^FDHello^FS^XZ");
        try
        {
            Assert.True(FileValidator.Validate(path, "application/vnd.zebra.zpl", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validates_zpl_with_leading_whitespace()
    {
        var path = WriteTempFile("\n  ^XA^FO50,50^FDTest^FS^XZ");
        try
        {
            Assert.True(FileValidator.Validate(path, "application/vnd.zebra.zpl", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validates_zpl_tilde_command()
    {
        // ~EG is a safe tilde command (erase graphic — removes from buffer, not flash)
        var path = WriteTempFile("~EG^XA^FO50,50^FDTest^FS^XZ");
        try
        {
            Assert.True(FileValidator.Validate(path, "application/vnd.zebra.zpl", out _));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("^XA^XB^XZ", "^XB")]                                       // firmware download
    [InlineData("^XA^IDR:LABEL.ZPL^XZ", "^ID")]                            // delete stored object
    [InlineData("^XA^ILR:LABEL.ZPL^XZ", "^IL")]                            // delete stored label
    [InlineData("^XA^FO10,10^FDhello^FS~JR", "~JR")]                       // power-cycle reset
    [InlineData("^XA~JB^XZ", "~JB")]                                       // factory reset
    [InlineData("^XA~DGR:SAMPLE.GRF,1,1,FF^XZ", "~DG")]                    // download graphic to memory
    [InlineData("^XA~DYR:FILE.TTF,A,0,,data^XZ", "~DY")]                   // download objects to flash
    [InlineData("^XA^WFR:FIRMWARE.ZPL^XZ", "^WF")]                         // write firmware
    [InlineData("^XA~HS^XZ", "~HS")]                                       // host status info leak
    [InlineData("^XA~HI^XZ", "~HI")]                                       // host identification info leak
    [InlineData("^XA~HD^XZ", "~HD")]                                       // head diagnostic info leak
    [InlineData("^XA~WC^XZ", "~WC")]                                       // print config (waste labels)
    [InlineData("^XA~WR^XZ", "~WR")]                                       // write RFID
    public void Rejects_zpl_with_dangerous_commands(string zpl, string blockedCommand)
    {
        var path = WriteTempFile(zpl);
        try
        {
            Assert.False(FileValidator.Validate(path, "application/vnd.zebra.zpl", out var reason));
            Assert.Contains("blocked command", reason);
            Assert.Contains(blockedCommand, reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_dangerous_zpl_commands_case_insensitively()
    {
        var path = WriteTempFile("^XA^xb^XZ");
        try
        {
            Assert.False(FileValidator.Validate(path, "application/vnd.zebra.zpl", out var reason));
            Assert.Contains("blocked command", reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Allows_safe_zpl_label()
    {
        // A typical shipping label with common safe commands
        var zpl = "^XA^FO50,50^ADN,36,20^FDShip To:^FS^FO50,100^BY3^BCN,100,Y,N,N^FD12345678^FS^PQ2^XZ";
        var path = WriteTempFile(zpl);
        try
        {
            Assert.True(FileValidator.Validate(path, "application/vnd.zebra.zpl", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_non_zpl_as_zpl()
    {
        var path = WriteTempFile("This is just regular text, not ZPL");
        try
        {
            Assert.False(FileValidator.Validate(path, "application/vnd.zebra.zpl", out var reason));
            Assert.Contains("does not match", reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validates_png_magic_bytes()
    {
        var path = WriteTempBytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
        try
        {
            Assert.True(FileValidator.Validate(path, "image/png", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validates_jpeg_magic_bytes()
    {
        var path = WriteTempBytes([0xFF, 0xD8, 0xFF, 0xE0, 0x00]);
        try
        {
            Assert.True(FileValidator.Validate(path, "image/jpeg", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_wrong_magic_bytes_for_png()
    {
        var path = WriteTempBytes([0xFF, 0xD8, 0xFF, 0xE0]); // JPEG bytes, not PNG
        try
        {
            Assert.False(FileValidator.Validate(path, "image/png", out var reason));
            Assert.Contains("does not match", reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_empty_file()
    {
        var path = WriteTempBytes([]);
        try
        {
            Assert.False(FileValidator.Validate(path, "image/png", out var reason));
            Assert.Contains("empty", reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Text_plain_accepts_printable_ascii()
    {
        var path = WriteTempFile("Hello, World!\r\n\tIndented line.");
        try
        {
            Assert.True(FileValidator.Validate(path, "text/plain", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Text_plain_rejects_binary_content()
    {
        var path = WriteTempBytes([0x00, 0x01, 0x02, 0x80, 0xFF]);
        try
        {
            Assert.False(FileValidator.Validate(path, "text/plain", out var reason));
            Assert.Contains("does not match", reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_unknown_content_type()
    {
        var path = WriteTempFile("whatever");
        try
        {
            Assert.False(FileValidator.Validate(path, "application/octet-stream", out var reason));
            Assert.Contains("No validator", reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validates_pdf_magic_bytes()
    {
        var path = WriteTempBytes([0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34]); // %PDF-1.4
        try
        {
            Assert.True(FileValidator.Validate(path, "application/pdf", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_non_pdf_bytes_as_pdf()
    {
        var path = WriteTempBytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]); // PNG bytes, not PDF
        try
        {
            Assert.False(FileValidator.Validate(path, "application/pdf", out var reason));
            Assert.Contains("does not match", reason);
        }
        finally { File.Delete(path); }
    }

    private static string WriteTempFile(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    private static string WriteTempBytes(byte[] content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        return path;
    }
}
