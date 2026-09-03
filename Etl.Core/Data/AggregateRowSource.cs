using System.Runtime.CompilerServices;
using Etl.Core.Abstractions;

namespace Etl.Core.Data;

/// <summary>
/// A Microsoft.Aggregate transform (GroupBy + Count), built speculatively 2026-08-30 -- see the
/// SSIS-to-C# generator's own <c>AggregatePayload</c> doc comment for why (the one real evidenced
/// instance, RBC_Demo_ETL's own <c>AGG_ByRegion</c>, sits downstream of a Lookup that already
/// blocks the whole flow regardless of Aggregate support, so this exists for completeness, not to
/// close a real gap).
///
/// Unlike every other <see cref="IRowSource{TRow}"/> in this project, this one is BLOCKING on its
/// input side: it must read every row from <paramref name="source"/> before it can produce its
/// first output row (a group's row/count isn't known until every row that belongs to it has been
/// seen). It still satisfies the plain streaming <see cref="IRowSource{TRow}"/> contract on its
/// OUTPUT side -- one row per distinct key, in first-seen order -- so it slots directly into the
/// existing <c>DataFlowStep{TRow,TEntity}</c> pipeline with no changes to that type at all.
///
/// <paramref name="project"/> is where the actual aggregation happens (e.g. counting non-null
/// values of a specific column) -- this type only groups; it has no built-in notion of "Count"
/// or any other aggregation function, so the generated code decides exactly what each group
/// becomes.
/// </summary>
public sealed class AggregateRowSource<TSourceRow, TKey, TRow>(
    string name,
    IRowSource<TSourceRow> source,
    Func<TSourceRow, TKey> keySelector,
    Func<TKey, IReadOnlyList<TSourceRow>, TRow> project,
    IEqualityComparer<TKey>? keyComparer = null) : IRowSource<TRow>
    where TKey : notnull
{
    public string Name => name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var groups = new Dictionary<TKey, List<TSourceRow>>(keyComparer ?? EqualityComparer<TKey>.Default);
        var order = new List<TKey>();

        await foreach (var row in source.ReadAsync(ct))
        {
            var key = keySelector(row);
            if (!groups.TryGetValue(key, out var rows))
            {
                rows = [];
                groups[key] = rows;
                order.Add(key);
            }
            rows.Add(row);
        }

        foreach (var key in order)
            yield return project(key, groups[key]);
    }
}
