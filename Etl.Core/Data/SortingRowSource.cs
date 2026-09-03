using System.Runtime.CompilerServices;
using Etl.Core.Abstractions;

namespace Etl.Core.Data;

/// <summary>
/// Wraps another <see cref="IRowSource{TRow}"/>, buffering it fully into memory and yielding its
/// rows back out ordered by <paramref name="key"/> -- SSIS's own Sort component, modeled as a
/// decorator rather than a new step type (matching <see cref="FilteringRowSource{TRow}"/>'s own
/// precedent), so it slots into the existing Source-&gt;Transform-&gt;Sink pipeline with zero
/// changes to <c>DataFlowStep&lt;TRow,TEntity&gt;</c>.
///
/// Extracted (gap-audit Phase 3.5, 2026-09-02) from <see cref="MergeJoinRowSource{TLeft,TRight,TKey,TRow}"/>'s
/// own private <c>CollectSortedAsync</c> helper, which now calls this type internally instead of
/// duplicating the sort -- a real SSIS Merge Join always sits downstream of a Sort on each of its
/// own two inputs, so both consumers share the identical "sort fully in memory" behavior. Buffers
/// the whole source before yielding anything (a true streaming external sort is not needed at this
/// PoC's row counts, same tradeoff <see cref="MergeJoinRowSource{TLeft,TRight,TKey,TRow}"/> already
/// made before this extraction).
/// </summary>
public sealed class SortingRowSource<TRow, TKey>(
    string name,
    IRowSource<TRow> source,
    Func<TRow, TKey> key,
    IComparer<TKey> comparer) : IRowSource<TRow>
{
    public string Name => name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var list = new List<TRow>();
        await foreach (var row in source.ReadAsync(ct)) list.Add(row);
        list.Sort((a, b) => comparer.Compare(key(a), key(b)));

        foreach (var row in list)
        {
            ct.ThrowIfCancellationRequested();
            yield return row;
        }
    }
}
