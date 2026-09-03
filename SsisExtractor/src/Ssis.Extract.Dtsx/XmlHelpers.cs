using System.Globalization;
using System.Xml.Linq;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Small, deliberately permissive attribute-reading helpers. The DTS/SSIS schemas only
/// serialize an attribute when its value differs from the schema default, so "absent"
/// is a normal, meaningful state (not a parse error) throughout these readers -- every
/// helper here returns null/false rather than throwing when an attribute is missing.
/// </summary>
internal static class XmlHelpers
{
    public static string? Attr(this XElement el, XName name) => (string?)el.Attribute(name);

    public static int? AttrInt(this XElement el, XName name)
    {
        var raw = el.Attr(name);
        return raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static long? AttrLong(this XElement el, XName name)
    {
        var raw = el.Attr(name);
        return raw is not null && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// The DTS/SSIS schemas mix "True"/"False" (most attributes) and "1"/"0" (a few,
    /// e.g. Project.params' Required/Sensitive) for booleans. Accepts both.
    /// </summary>
    public static bool? AttrBool(this XElement el, XName name)
    {
        var raw = el.Attr(name);
        if (raw is null) return null;
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1") return true;
        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0") return false;
        return null;
    }

    public static bool AttrBoolOrFalse(this XElement el, XName name) => el.AttrBool(name) ?? false;
}
