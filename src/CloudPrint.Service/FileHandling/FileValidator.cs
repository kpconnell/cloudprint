using System.Text.RegularExpressions;

namespace CloudPrint.Service.FileHandling;

public static partial class FileValidator
{
    private static readonly Dictionary<string, Func<byte[], bool>> HeaderValidators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/vnd.zebra.zpl"] = IsZplHeader,
        ["text/plain"] = IsPrintableAscii,
        ["image/png"] = bytes => HasMagicBytes(bytes, [0x89, 0x50, 0x4E, 0x47]),
        ["image/jpeg"] = bytes => HasMagicBytes(bytes, [0xFF, 0xD8, 0xFF]),
        ["image/bmp"] = bytes => HasMagicBytes(bytes, [0x42, 0x4D]),
        ["image/gif"] = bytes => HasMagicBytes(bytes, [0x47, 0x49, 0x46]),
        ["image/tiff"] = bytes => HasMagicBytes(bytes, [0x49, 0x49, 0x2A, 0x00]) ||
                                  HasMagicBytes(bytes, [0x4D, 0x4D, 0x00, 0x2A]),
    };

    // ZPL commands that can modify printer firmware, configuration, or stored objects.
    // Matched case-insensitively against the full file content.
    // ^XB  — firmware download
    // ^ID  — delete stored object
    // ^IL  — delete stored label
    // ~JR  — power-cycle reset
    // ~JB  — reset factory defaults
    // ^JH  — early warning settings
    // ~WR  — write RFID
    // ~HS  — host status (info leak)
    // ~HI  — host identification (info leak)
    // ~HD  — head diagnostic (info leak)
    // ~WC  — print config to label (wastes labels)
    // ^WF  — write firmware to flash
    // ~DY  — download objects (firmware, fonts, graphics to flash)
    // ~DG  — download graphic to printer memory
    [GeneratedRegex(
        @"(\^XB|\^ID|\^IL|~JR|~JB|\^JH|~WR|~HS|~HI|~HD|~WC|\^WF|~DY|~DG)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DangerousZplCommandPattern();

    public static bool Validate(string filePath, string contentType, out string reason)
    {
        reason = string.Empty;

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            reason = "File is empty (0 bytes)";
            return false;
        }

        if (!HeaderValidators.TryGetValue(contentType, out var validator))
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

        // ZPL requires full-content scan for dangerous commands
        if (string.Equals(contentType, "application/vnd.zebra.zpl", StringComparison.OrdinalIgnoreCase))
        {
            var zplContent = File.ReadAllText(filePath);
            var match = DangerousZplCommandPattern().Match(zplContent);
            if (match.Success)
            {
                reason = $"ZPL contains blocked command '{match.Value}' — only label-printing commands are allowed";
                return false;
            }
        }

        return true;
    }

    private static bool IsPrintableAscii(byte[] header)
    {
        // Verify the header bytes are printable ASCII (0x20-0x7E) or common whitespace (tab, CR, LF)
        foreach (var b in header)
        {
            if (b >= 0x20 && b <= 0x7E) continue; // printable
            if (b == 0x09 || b == 0x0A || b == 0x0D) continue; // tab, LF, CR
            return false;
        }
        return true;
    }

    private static bool IsZplHeader(byte[] header)
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
