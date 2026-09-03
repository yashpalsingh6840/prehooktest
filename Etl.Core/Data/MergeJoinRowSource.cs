using System.Runtime.CompilerServices;
using Etl.Core.Abstractions;

namespace Etl.Core.Data;

/// <summary>
/// A Merge Join transformation, fed by two already-modeled <see cref="IRowSource{TRow}"/>s
/// (matching a real SSIS Merge Join's own Left/Right Input) -- implemented as an
/// <see cref="IRowSource{TRow}"/> itself, not a separate step type, so it slots directly into
/// the existing <c>DataFlowStep&lt;TRow,TEntity&gt;</c> (Source -&gt; Transform -&gt; Sink)
/// pipeline with zero changes to that type: everything downstream of a Merge Join (Derived
/// Column, a plain passthrough transform, the destination) just sees an ordinary <c>TRow</c>,
/// exactly like a SQL or CSV row.
///
/// SSIS's own Merge Join REQUIRES both inputs to already be sorted by the join key (that's the
/// whole point of the Sort component always preceding it in a real package) -- this
/// implementation does that sorting itself, via <see cref="SortingRowSource{TRow,TKey}"/>
/// (extracted from this type's own original private sort helper -- gap-audit Phase 3.5,
/// 2026-09-02, once a standalone Sort needed the identical behavior), rather than assuming the
/// caller already sorted. Buffers both full sides into memory before joining; fine for this
/// PoC's row counts, not a streaming merge against a pre-sorted database cursor.
///
/// Duplicate keys on either side are handled correctly (the standard sort-merge join algorithm:
/// an equal-key run on each side is matched as a full cross product), not just a naive 1:1
/// zip -- SSIS's own Merge Join does the same for a non-unique join key.
/// </summary>
public sealed class MergeJoinRowSource<TLeft, TRight, TKey, TRow>(
    string name,
    IRowSource<TLeft> leftSource,
    IRowSource<TRight> rightSource,
    Func<TLeft, TKey> leftKey,
    Func<TRight, TKey> rightKey,
    IComparer<TKey> keyComparer,
    MergeJoinType joinType,
    Func<TLeft?, TRight?, TRow> project) : IRowSource<TRow>
    where TLeft : class
    where TRight : class
{
    public string Name => name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var left = await CollectAsync(new SortingRowSource<TLeft, TKey>($"{name}.Left", leftSource, leftKey, keyComparer), ct);
        var right = await CollectAsync(new SortingRowSource<TRight, TKey>($"{name}.Right", rightSource, rightKey, keyComparer), ct);

        var i = 0;
        var j = 0;
        while (i < left.Count && j < right.Count)
        {
            var cmp = keyComparer.Compare(leftKey(left[i]), rightKey(right[j]));
            if (cmp < 0)
            {
                if (joinType != MergeJoinType.Inner) yield return project(left[i], null);
                i++;
            }
            else if (cmp > 0)
            {
                if (joinType == MergeJoinType.FullOuter) yield return project(null, right[j]);
                j++;
            }
            else
            {
                // An equal-key run on each side matches as a full cross product -- correct for a
                // non-unique join key, not just an assumed 1:1 match.
                var leftStart = i;
                while (i < left.Count && keyComparer.Compare(leftKey(left[i]), leftKey(left[leftStart])) == 0) i++;
                var rightStart = j;
                while (j < right.Count && keyComparer.Compare(rightKey(right[j]), rightKey(right[rightStart])) == 0) j++;

                for (var li = leftStart; li < i; li++)
                    for (var ri = rightStart; ri < j; ri++)
                        yield return project(left[li], right[ri]);
            }
        }

        if (joinType != MergeJoinType.Inner)
            for (; i < left.Count; i++)
                yield return project(left[i], null);

        if (joinType == MergeJoinType.FullOuter)
            for (; j < right.Count; j++)
                yield return project(null, right[j]);
    }

    private static async Task<List<T>> CollectAsync<T>(IRowSource<T> source, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var row in source.ReadAsync(ct)) list.Add(row);
        return list;
    }
}
