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
        var path = WriteTempFile("~DG000.GRF,1,1,FF");
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
    public void Text_plain_accepts_anything()
    {
        var path = WriteTempFile("literally anything");
        try
        {
            Assert.True(FileValidator.Validate(path, "text/plain", out _));
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
