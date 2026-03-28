namespace CloudPrint.Service.FileHandling;

public static class FileValidator
{
    private static readonly Dictionary<string, Func<byte[], bool>> Validators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/vnd.zebra.zpl"] = IsZpl,
        ["text/plain"] = _ => true, // any bytes are valid text
        ["image/png"] = bytes => HasMagicBytes(bytes, [0x89, 0x50, 0x4E, 0x47]),
        ["image/jpeg"] = bytes => HasMagicBytes(bytes, [0xFF, 0xD8, 0xFF]),
        ["image/bmp"] = bytes => HasMagicBytes(bytes, [0x42, 0x4D]),
        ["image/gif"] = bytes => HasMagicBytes(bytes, [0x47, 0x49, 0x46]),
        ["image/tiff"] = bytes => HasMagicBytes(bytes, [0x49, 0x49, 0x2A, 0x00]) ||
                                  HasMagicBytes(bytes, [0x4D, 0x4D, 0x00, 0x2A]),
    };

    public static bool Validate(string filePath, string contentType, out string reason)
    {
        reason = string.Empty;

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            reason = "File is empty (0 bytes)";
            return false;
        }

        if (!Validators.TryGetValue(contentType, out var validator))
        {
            reason = $"No validator for content type '{contentType}'";
            return false;
        }

        var header = new byte[Math.Min(16, fileInfo.Length)];
        using var fs = File.OpenRead(filePath);
        _ = fs.Read(header, 0, header.Length);

        if (!validator(header))
        {
            var headerHex = BitConverter.ToString(header[..Math.Min(8, header.Length)]);
            reason = $"File header [{headerHex}] does not match expected format for {contentType}";
            return false;
        }

        return true;
    }

    private static bool IsZpl(byte[] header)
    {
        // ZPL typically starts with ^XA, CT~~, or ~
        // Check for common ZPL start sequences (ASCII)
        if (header.Length < 2)
            return false;

        // ^XA
        if (header[0] == 0x5E && header[1] == 0x58)
            return true;

        // CT~~ (continuous tone)
        if (header.Length >= 4 && header[0] == 0x43 && header[1] == 0x54 && header[2] == 0x7E && header[3] == 0x7E)
            return true;

        // ~ commands (like ~DG for download graphic)
        if (header[0] == 0x7E)
            return true;

        // Some ZPL files start with whitespace/newlines before ^XA
        var text = System.Text.Encoding.ASCII.GetString(header);
        if (text.TrimStart().StartsWith("^"))
            return true;

        return false;
    }

    private static bool HasMagicBytes(byte[] header, byte[] magic)
    {
        if (header.Length < magic.Length)
            return false;

        for (var i = 0; i < magic.Length; i++)
        {
            if (header[i] != magic[i])
                return false;
        }

        return true;
    }
}
