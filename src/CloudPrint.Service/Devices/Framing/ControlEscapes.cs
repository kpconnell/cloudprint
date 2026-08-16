using System.Globalization;
using System.Text;

namespace CloudPrint.Service.Devices.Framing;

/// <summary>
/// Decodes the escape syntax users type into config fields (request/init commands, frame delimiters,
/// command terminators) so control characters can be expressed in JSON and in the configurator UI:
///   \xHH  \uHHHH  \r \n \t \0 \e \\   and mnemonics  &lt;STX&gt; &lt;ETX&gt; &lt;CR&gt; &lt;LF&gt; &lt;ENQ&gt; ...
/// Anything else is passed through literally, so plain commands like "W" or "S" are unaffected.
/// </summary>
public static class ControlEscapes
{
    private static readonly Dictionary<string, char> Mnemonics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NUL"] = '\0', ["SOH"] = '\x01', ["STX"] = '\x02', ["ETX"] = '\x03', ["EOT"] = '\x04',
        ["ENQ"] = '\x05', ["ACK"] = '\x06', ["BEL"] = '\x07', ["BS"] = '\x08', ["TAB"] = '\t', ["HT"] = '\t',
        ["LF"] = '\n', ["VT"] = '\x0B', ["FF"] = '\x0C', ["CR"] = '\r', ["SO"] = '\x0E', ["SI"] = '\x0F',
        ["DLE"] = '\x10', ["DC1"] = '\x11', ["XON"] = '\x11', ["DC2"] = '\x12', ["DC3"] = '\x13', ["XOFF"] = '\x13',
        ["DC4"] = '\x14', ["NAK"] = '\x15', ["SYN"] = '\x16', ["ETB"] = '\x17', ["CAN"] = '\x18', ["EM"] = '\x19',
        ["SUB"] = '\x1A', ["ESC"] = '\x1B', ["FS"] = '\x1C', ["GS"] = '\x1D', ["RS"] = '\x1E', ["US"] = '\x1F',
        ["SP"] = ' ', ["DEL"] = '\x7F'
    };

    public static string Unescape(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (text.IndexOf('\\') < 0 && text.IndexOf('<') < 0)
            return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '<')
            {
                var close = text.IndexOf('>', i + 1);
                if (close > i + 1 && close - i - 1 <= 4 && Mnemonics.TryGetValue(text.Substring(i + 1, close - i - 1), out var mn))
                {
                    sb.Append(mn);
                    i = close;
                    continue;
                }
                sb.Append(c);
                continue;
            }

            if (c != '\\' || i == text.Length - 1)
            {
                sb.Append(c);
                continue;
            }

            var next = text[i + 1];
            switch (next)
            {
                case 'x' when i + 3 < text.Length && TryHex(text.AsSpan(i + 2, 2), out var hx):
                    sb.Append((char)hx); i += 3; break;
                case 'u' when i + 5 < text.Length && TryHex(text.AsSpan(i + 2, 4), out var ux):
                    sb.Append((char)ux); i += 5; break;
                case 'r': sb.Append('\r'); i++; break;
                case 'n': sb.Append('\n'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case '0': sb.Append('\0'); i++; break;
                case 'e': sb.Append('\x1B'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                default: sb.Append(c); break; // unknown escape: keep the backslash literally
            }
        }
        return sb.ToString();
    }

    /// <summary>Renders control characters as mnemonics for logs and UI ("\x02M\x03" → "&lt;STX&gt;M&lt;ETX&gt;").</summary>
    public static string Describe(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c < 0x20 || c == 0x7F)
            {
                var name = Mnemonics.FirstOrDefault(kv => kv.Value == c && kv.Key.Length <= 3 && kv.Key is not ("HT" or "XON" or "XOFF")).Key;
                sb.Append('<').Append(name ?? $"x{(int)c:X2}").Append('>');
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool TryHex(ReadOnlySpan<char> span, out int value) =>
        int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
}
