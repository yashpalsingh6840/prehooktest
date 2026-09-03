using System.Runtime.CompilerServices;
using Etl.Core.Abstractions;

namespace Etl.Core.Data;

/// <summary>
/// A <c>Microsoft.Merge</c> component (NOT Merge Join -- Merge combines two already-sorted,
/// matching-schema inputs into one row stream in sorted-key order; it never joins mismatched
/// schemas the way Merge Join does) -- gap-audit Phase 3.6 (2026-09-02), the single most
/// important probe in that whole phase per its own plan.
///
/// <para><b>A real dtexec run settled the central open question, not a guess:</b> two
/// independently-sorted sources with deliberately DISJOINT, INTERLEAVED key ranges (Left:
/// 1,3,5; Right: 2,4,6) landed in a Flat File Destination as <c>1,2,3,4,5,6</c> -- a TRUE
/// sort-preserving two-pointer interleave, not plain concatenation-after-independent-sorts
/// (which would have read <c>1,3,5,2,4,6</c>). See
/// <c>Ssis.Extract.FixtureBuilder.Program.BuildMergeInterleaveProbeFixture</c>'s own doc
/// comment for the full fixture and measurement.</para>
///
/// Sorts both sides itself via <see cref="SortingRowSource{TRow,TKey}"/> (SSIS's own Merge
/// requires pre-sorted input -- that is the whole reason a Sort always precedes it in a real
/// package, same requirement <see cref="MergeJoinRowSource{TLeft,TRight,TKey,TRow}"/> already
/// has for its own two inputs), then walks both sorted lists with a plain two-pointer merge,
/// essentially <see cref="MergeJoinRowSource{TLeft,TRight,TKey,TRow}"/> minus the key-match
/// test -- every row from BOTH sides is always emitted, exactly once, in overall sorted order.
///
/// <para><b>Tied-key ordering is a stated choice, not an independently dtexec-measured one:</b>
/// the real probe used disjoint key ranges specifically to settle interleave-vs-concatenation
/// unambiguously, and never exercised a genuine tie between the two sides. This implementation
/// emits the LEFT row first on a tie, matching <see cref="MergeJoinRowSource{TLeft,TRight,TKey,TRow}"/>'s
/// own left-biased convention for a tied comparison, but real SSIS's own tie-breaking rule for
/// <c>Microsoft.Merge</c> specifically has not been measured.</para>
///
/// Reuses <typeparamref name="TRow"/> for BOTH sides -- unlike a join, Merge combines rows that
/// already share the exact same schema by construction (real SSIS enforces this at design time),
/// so there is no separate TLeft/TRight the way <see cref="MergeJoinRowSource{TLeft,TRight,TKey,TRow}"/>
/// needs.
/// </summary>
public sealed class MergeInterleaveRowSource<TRow, TKey>(
    string name,
    IRowSource<TRow> leftSource,
    IRowSource<TRow> rightSource,
    Func<TRow, TKey> keySelector,
    IComparer<TKey> keyComparer) : IRowSource<TRow>
{
    public string Name => name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var left = await CollectAsync(new SortingRowSource<TRow, TKey>($"{name}.Left", leftSource, keySelector, keyComparer), ct);
        var right = await CollectAsync(new SortingRowSource<TRow, TKey>($"{name}.Right", rightSource, keySelector, keyComparer), ct);

        var i = 0;
        var j = 0;
        while (i < left.Count && j < right.Count)
        {
            var cmp = keyComparer.Compare(keySelector(left[i]), keySelector(right[j]));
            if (cmp <= 0)
            {
                yield return left[i];
                i++;
            }
            else
            {
                yield return right[j];
                j++;
            }
        }

        for (; i < left.Count; i++) yield return left[i];
        for (; j < right.Count; j++) yield return right[j];
    }

    private static async Task<List<TRow>> CollectAsync(IRowSource<TRow> source, CancellationToken ct)
    {
        var list = new List<TRow>();
        await foreach (var row in source.ReadAsync(ct)) list.Add(row);
        return list;
    }
}
