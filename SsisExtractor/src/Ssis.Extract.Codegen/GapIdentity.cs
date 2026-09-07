using System.Security.Cryptography;
using System.Text;
using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Turns a <see cref="GenerationGap"/> into a stable, addressable work item: its
/// <see cref="GapSpec.GapId"/> and its <see cref="GapTier"/>.
///
/// <b>The id format deliberately mirrors <c>ConformanceRuleSpec.RuleId</c></b>
/// (<c>&lt;CATEGORY&gt;:&lt;location&gt;</c>) rather than inventing a second convention -- both
/// are read by humans in a report and typed into a hand-maintained file keyed by them, and this
/// project already settled that a readable id beats a hash for exactly that reason. The stability
/// contract is the same and equally load-bearing: derived only from meaning-bearing identifiers,
/// never from document order, GUIDs, or counts, because a fill keyed by an id that moved when
/// someone nudged the package in the designer is a silently orphaned fill.
/// </summary>
public static class GapIdentity
{
    /// <summary>
    /// The token that opens a <see cref="GapSpec.GapId"/>. Not <c>Kind.ToString()</c> -- these are
    /// part of a persisted, hand-typed contract, so they are pinned here explicitly and can never
    /// drift by someone renaming an enum member.
    /// </summary>
    public static string KindToken(GapKind kind) => kind switch
    {
        GapKind.ScriptTask => "SCRIPT-TASK",
        GapKind.ScriptComponentColumn => "SCRIPT-COLUMN",
        GapKind.LookupJoinKey => "LOOKUP-JOIN-KEY",
        GapKind.ConditionalConstraint => "CONSTRAINT",
        GapKind.EncryptedConnectionManagerSecret => "ENCRYPTED-SECRET",
        GapKind.TestOracle => "TEST-ORACLE",
        GapKind.LocalFileSourceData => "LOCAL-DATA",
        _ => "GAP",
    };

    /// <summary>
    /// Tier is DERIVED, never stored twice -- an explicitly classified kind maps to its own tier;
    /// everything else falls back to <see cref="GenerationGap.IsBlocking"/> (a non-blocking gap is
    /// an advisory, a blocking one this tool never classified is missing tool support). This is
    /// what keeps the classification change to a handful of call sites.
    /// </summary>
    public static GapTier TierOf(GenerationGap gap) => gap.Kind switch
    {
        GapKind.LookupJoinKey or GapKind.EncryptedConnectionManagerSecret
            or GapKind.TestOracle or GapKind.LocalFileSourceData => GapTier.MissingDatum,
        GapKind.ScriptTask or GapKind.ScriptComponentColumn => GapTier.MissingLogic,
        _ => gap.IsBlocking ? GapTier.MissingToolSupport : GapTier.Advisory,
    };

    /// <summary>Only Tier 1/2 gaps get a work packet. Tier 3 deliberately does NOT -- see <see cref="GapTier.MissingToolSupport"/>'s own doc comment.</summary>
    public static bool HasWorkPacket(GenerationGap gap) =>
        TierOf(gap) is GapTier.MissingDatum or GapTier.MissingLogic;

    /// <summary>
    /// <c>&lt;KIND&gt;:&lt;Package&gt;:&lt;Location&gt;</c>. Two gaps in one package genuinely
    /// sharing a kind AND a location are disambiguated by a short hash of the REASON text rather
    /// than by their position in the list -- order would break the stability contract above the
    /// first time an emitter reported its gaps in a different sequence.
    /// </summary>
    public static string ComputeId(string packageName, GenerationGap gap, bool disambiguate = false)
    {
        var id = ComputeId(packageName, gap.Kind, gap.Location);
        return disambiguate ? $"{id}~{ShortHash(gap.Reason)}" : id;
    }

    /// <summary>The same id, computed from its parts -- for a caller that needs to look a decision
    /// up BEFORE it knows whether it will end up reporting a gap at all (see PackageGenerator's
    /// own Lookup gate, which asks "is this one already decided?" first).</summary>
    public static string ComputeId(string packageName, GapKind kind, string location) =>
        $"{KindToken(kind)}:{packageName}:{location}";

    /// <summary>
    /// Assigns every gap in one package its final id, disambiguating only the ids that actually
    /// collide. An exact duplicate (same kind, location AND reason) is a genuine duplicate report
    /// rather than two work items, so it collapses to one entry.
    /// </summary>
    public static List<(string GapId, GenerationGap Gap)> AssignIds(string packageName, IEnumerable<GenerationGap> gaps)
    {
        var distinct = new List<GenerationGap>();
        var seen = new HashSet<(string, string, GapKind)>();
        foreach (var gap in gaps)
        {
            if (seen.Add((gap.Location, gap.Reason, gap.Kind))) distinct.Add(gap);
        }

        var byBaseId = distinct.GroupBy(g => ComputeId(packageName, g)).ToDictionary(g => g.Key, g => g.Count());

        var result = new List<(string, GenerationGap)>();
        foreach (var gap in distinct)
        {
            var baseId = ComputeId(packageName, gap);
            result.Add((ComputeId(packageName, gap, disambiguate: byBaseId[baseId] > 1), gap));
        }
        return result;
    }

    /// <summary>A GapId is used as a FILE NAME for its own work packet, and ':' is not legal in one on Windows.</summary>
    public static string ToFileName(string gapId) => gapId.Replace(':', '_').Replace('~', '_');

    public static string Sha256(string text) =>
        // ToHexStringLower is .NET 9+; this project targets net8.0.
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string ShortHash(string text) => Sha256(text)[..8];
}
