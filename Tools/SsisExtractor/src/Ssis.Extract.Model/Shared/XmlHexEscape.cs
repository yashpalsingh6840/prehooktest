using System.Text;
using System.Text.RegularExpressions;

namespace Ssis.Extract.Model.Shared;

/// <summary>
/// Decodes SSIS's "_xHHHH_" hexadecimal character escaping, used for delimiter values
/// (row/column delimiters, text qualifiers) inside a flat-file connection manager's
/// ObjectData -- e.g. <c>_x002C_</c> is a comma, <c>_x000D__x000A_</c> is CRLF. SSIS uses
/// this so control/reserved characters can round-trip through XML attribute values.
/// Decoding this is what makes CLAUDE.md trap 2 (CRLF row delimiter vs an LF source file
/// producing a silent 0-row load) visible in the spec instead of buried in an opaque
/// escaped string.
/// </summary>
public static partial class XmlHexEscape
{
    [GeneratedRegex("_x([0-9A-Fa-f]{4})_")]
    private static partial Regex EscapePattern();

    public static string Decode(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw ?? string.Empty;
        }

        return EscapePattern().Replace(raw, m =>
        {
            var code = int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
            return ((char)code).ToString();
        });
    }

    /// <summary>
    /// Human-readable form for control characters that would otherwise render invisibly
    /// in a JSON string (e.g. "\r\n" printed literally as a return+newline). Used only for
    /// the display-friendly field; the decoded raw value is kept separately.
    /// </summary>
    public static string ToDisplayForm(string decoded)
    {
        var sb = new StringBuilder();
        foreach (var ch in decoded)
        {
            switch (ch)
            {
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}
