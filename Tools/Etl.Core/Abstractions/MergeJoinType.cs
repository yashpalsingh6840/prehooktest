namespace Etl.Core.Abstractions;

/// <summary>
/// SSIS's own Merge Join transformation join type.
///
/// <para><b>The raw-value mapping is MEASURED against real SSIS (2026-09-02), superseding an
/// earlier inference recorded here that was wrong.</b> A fixture joining left keys {1,2,3} to
/// right keys {2,3,4} was run under dtexec once per raw value, so row count alone identifies
/// the semantics:</para>
/// <list type="bullet">
/// <item><c>0</c> -> 4 rows (both unmatched sides kept) = <see cref="FullOuter"/></item>
/// <item><c>1</c> -> 3 rows (unmatched left kept, unmatched right dropped) = <see cref="LeftOuter"/></item>
/// <item><c>2</c> -> 2 rows (matches only) = <see cref="Inner"/></item>
/// </list>
/// <para>The previous comment here claimed raw 2 was <see cref="LeftOuter"/>, inferred from the
/// real destination's column shape and the property's description text. It is <see cref="Inner"/>.
/// Because the codegen layer also hardcoded <c>LeftOuter</c> for every Merge Join regardless of
/// the raw value, the one real evidenced package generated an inner join as a left outer one --
/// emitting unmatched left rows, with a nulled right side, that real SSIS drops. The "verified
/// via a real generated run" claim was circular: the run exercised generated code that had
/// LeftOuter compiled into it.</para>
///
/// <para>Note the raw values run in the exact REVERSE of this enum's own ordinal order, so a
/// cast from the raw value is wrong for 0 and 2 and right only for 1. The mapping is therefore
/// explicit in <c>PackagePlanner.ResolveMergeJoinType</c>. Codegen currently emits
/// <see cref="Inner"/> and <see cref="LeftOuter"/> only: <see cref="FullOuter"/> is understood
/// and implemented HERE, but the generated mapper accesses left-sourced columns as
/// <c>left!.Column</c>, which a full outer join violates for a right-only key, so that raw
/// value is reported as a gap rather than generated.</para>
///
/// <para>This type's own <see cref="MergeJoinRowSource{TLeft,TRight,TKey,TRow}"/> implements all
/// three correctly, independently covered by Etl.Core.Tests/Data/MergeJoinRowSourceTests.cs:
/// LeftOuter keeps an unmatched left row (nulled right side) and drops an unmatched right row
/// entirely, Inner keeps only matches, FullOuter keeps both unmatched sides, and duplicate keys
/// on either side match as a full cross product.</para>
/// </summary>
public enum MergeJoinType
{
    Inner,
    LeftOuter,
    FullOuter,
}
