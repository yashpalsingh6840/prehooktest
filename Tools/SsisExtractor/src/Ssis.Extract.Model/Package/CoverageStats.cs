namespace Ssis.Extract.Model.Package;

/// <summary>
/// The honesty check (plan §7.2): how much of this package's source XML ended up behind a
/// typed field vs verbatim in some <c>Unmapped</c>/<c>UnmappedTaskPayload</c>/
/// <c>DataFlowTaskMarker</c> raw-XML bag. Counted in XML *elements* (not elements +
/// attributes, unlike the plan's wording) -- a simplification worth knowing about: a
/// package could technically score high while still dropping attribute-level detail this
/// metric can't see. Good enough to answer "did we understand this file", not a substitute
/// for reading <c>Unmapped</c> itself when the percentage is below 100.
/// </summary>
public sealed class CoverageStats
{
    /// <summary>Every element in the source .dtsx, including the root.</summary>
    public required int TotalElements { get; init; }

    /// <summary>Elements inside a raw-XML fragment that a later build slice is expected to model (control flow already modeled this slice; pipeline content is the big one left, until slice 3).</summary>
    public required int UnmappedElements { get; init; }

    /// <summary>
    /// Elements inside a raw-XML fragment that is deliberately never modeled -- currently
    /// just <c>DTS:DesignTimeProperties</c> (pure GUI layout, explicitly documented by the
    /// file's own embedded comment as having no runtime effect). Excluded from both sides
    /// of the percentage so this permanent, intentional non-goal doesn't cap the score
    /// below 100% forever once everything meaningful actually is modeled.
    /// </summary>
    public required int ExcludedElements { get; init; }

    /// <summary>100 * (TotalElements - ExcludedElements - UnmappedElements) / (TotalElements - ExcludedElements), rounded to 2 decimal places. 100 when the denominator is 0 (nothing to cover).</summary>
    public required double CoveragePercent { get; init; }
}
