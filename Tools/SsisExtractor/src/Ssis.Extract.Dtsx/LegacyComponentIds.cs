using System.Text.RegularExpressions;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Normalizes a legacy-spelled pipeline <c>componentClassID</c> (from an SSIS 2005/2008-era
/// package that was never re-saved by a newer designer) to the canonical <c>Microsoft.X</c>
/// form every dispatch branch in <see cref="PipelineReader"/> matches against verbatim.
///
/// Without this, a legacy-spelled STOCK component (not a real third-party one) gets no
/// bespoke payload at all -- it silently falls through to the generic model -- AND is
/// actively MIS-flagged as <c>third-party-component</c> by <c>RulesEngine</c> (which tests
/// <c>StartsWith("Microsoft.")</c>). That is worse than merely incomplete: a client reading
/// the findings would be told to budget vendor-replacement effort for a component that is
/// actually a plain, fully-supported OLE DB Source.
///
/// Two real, independently-sourced legacy shapes exist and are handled differently:
///
/// 1. <b>Versioned ProgID strings</b> -- <c>DTSAdapter.&lt;Name&gt;.N</c> for source/
///    destination adapters, <c>DTSTransform.&lt;Name&gt;.N</c> for transforms. Confirmed
///    real (not guessed) via SSIS's own long-published documentation and forum evidence
///    (sqlis.com's "CreationName for SSIS 2005", Microsoft Learn's PipelineComponentInfos/
///    ComponentClassID references, and SSIS-2008-to-2014 upgrade threads) -- e.g.
///    <c>DTSAdapter.OLEDBSource.1</c>, <c>DTSTransform.DerivedColumn.2</c>,
///    <c>DTSTransform.Lookup.4</c>. The trailing <c>.N</c> is a per-component redesign
///    counter, not a globally consistent SSIS version number, and is confirmed to vary
///    (.1/.2/.4 all attested for different components/versions) -- so this table matches
///    on the stable <c>DTSAdapter.&lt;Name&gt;</c>/<c>DTSTransform.&lt;Name&gt;</c> prefix
///    with the trailing version segment stripped, rather than hardcoding one suffix.
///
/// 2. <b>Bare CLSIDs</b> -- some legacy-era packages persist a raw
///    <c>{8-4-4-4-12}</c> GUID instead of any ProgID string at all. Real GUIDs are
///    version-stable identifiers for the SAME component across SSIS releases, so a GUID
///    read from one real vintage package is trustworthy evidence for that GUID specifically
///    -- but is NOT extrapolated to any other component. Every entry below was read
///    directly from a real, published <c>.dtsx</c> file's own <c>componentClassID</c> and
///    <c>name</c> attributes, not guessed from a registry-key pattern or vendor
///    documentation. This table starts small and grows only from that same evidence bar --
///    see <see cref="TryNormalize"/>'s own fallback for what happens to an unrecognized
///    CLSID (an honest, named gap; never a guess from <c>contactInfo</c> free text, which
///    is vendor-supplied prose this tool does not trust to identify a component).
/// </summary>
internal static class LegacyComponentIds
{
    private static readonly Regex VersionedProgIdPattern = new(
        @"^(DTSAdapter|DTSTransform)\.(?<name>[A-Za-z0-9]+)(\.\d+)?$",
        RegexOptions.Compiled);

    private static readonly Regex BareClsidPattern = new(
        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>DTSAdapter.&lt;Name&gt;</c>/<c>DTSTransform.&lt;Name&gt;</c> (version suffix already
    /// stripped by <see cref="VersionedProgIdPattern"/>) -> canonical <c>Microsoft.X</c>.
    /// Ordinal-insensitive on the short name since the SSIS 2005-era registrations were not
    /// consistently cased across every source consulted.
    /// </summary>
    private static readonly Dictionary<string, string> VersionedProgIdMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OLEDBSource"] = "Microsoft.OLEDBSource",
        ["FlatFileSource"] = "Microsoft.FlatFileSource",
        ["DerivedColumn"] = "Microsoft.DerivedColumn",
        ["ConditionalSplit"] = "Microsoft.ConditionalSplit",
        ["UnionAll"] = "Microsoft.UnionAll",
        ["Lookup"] = "Microsoft.Lookup",
        ["Aggregate"] = "Microsoft.Aggregate",
        ["Sort"] = "Microsoft.Sort",
        ["Merge"] = "Microsoft.Merge",
        ["MergeJoin"] = "Microsoft.MergeJoin",
        ["Multicast"] = "Microsoft.Multicast",
    };

    /// <summary>
    /// Bare CLSID -> canonical <c>Microsoft.X</c>, each read directly from a real published
    /// <c>.dtsx</c> file's own <c>componentClassID</c>/<c>name</c> attribute pair
    /// (github.com/LearningTechStuff/SSIS-Tutorial, "Lesson 5.dtsx") -- not guessed.
    /// </summary>
    private static readonly Dictionary<string, string> ClsidMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{D23FD76B-F51D-420F-BBCB-19CBF6AC1AB4}"] = "Microsoft.FlatFileSource",
        ["{8DA75FED-1B7C-407D-B2AD-2B24209CCCA4}"] = "Microsoft.FlatFileDestination",
        ["{671046B0-AA63-4C9F-90E4-C06E0B710CE3}"] = "Microsoft.Lookup",
    };

    /// <summary>
    /// Attempts to resolve <paramref name="raw"/> to a canonical <c>Microsoft.X</c> component
    /// class ID. Returns <c>false</c> (and leaves <paramref name="canonical"/> null) both for a
    /// component that is already canonical (nothing to normalize) and for one this table has no
    /// evidence for -- callers distinguish "already fine" from "genuinely unknown" via
    /// <see cref="IsUnresolvedClsid"/>, not via this method's own return value.
    /// </summary>
    public static bool TryNormalize(string raw, out string canonical)
    {
        var match = VersionedProgIdPattern.Match(raw);
        if (match.Success && VersionedProgIdMap.TryGetValue(match.Groups["name"].Value, out var fromProgId))
        {
            canonical = fromProgId;
            return true;
        }

        if (ClsidMap.TryGetValue(raw, out var fromClsid))
        {
            canonical = fromClsid;
            return true;
        }

        canonical = raw;
        return false;
    }

    /// <summary>
    /// True when <paramref name="raw"/> is shaped like a bare CLSID (<c>{8-4-4-4-12}</c>) but
    /// matched no entry in <see cref="ClsidMap"/> -- the case that deserves an honest,
    /// human-actionable gap rather than silent generic-model handling, since a bare GUID gives
    /// a human reviewing <c>report</c>'s output no way to tell what the component actually is.
    /// </summary>
    public static bool IsUnresolvedClsid(string raw) => BareClsidPattern.IsMatch(raw) && !ClsidMap.ContainsKey(raw);
}
