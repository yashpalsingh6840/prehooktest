using System.Runtime.CompilerServices;
using Etl.Core.Abstractions;

namespace Etl.Core.Data;

/// <summary>
/// Wraps another <see cref="IRowSource{TRow}"/>, yielding only rows matching <paramref name="predicate"/>
/// -- built for a Lookup whose no-match rows are redirected away (SSIS's own <c>NoMatchBehavior=1</c>)
/// rather than failing the component. A redirected row must never reach anything downstream (a
/// transform's own join indexer, or an <see cref="AggregateRowSource{TSourceRow,TKey,TRow}"/>'s own
/// grouping) -- <see cref="AggregateRowSource{TSourceRow,TKey,TRow}"/> requires <c>TKey : notnull</c>,
/// so a miss can't simply be given a null/sentinel key and grouped separately; it has to be excluded
/// from the stream entirely, before grouping ever sees it. Once wrapped, every row that reaches a
/// consumer downstream is guaranteed to be a genuine match, so an ordinary throwing dictionary
/// indexer lookup (the same one a single-routed-output Lookup already uses) stays correct unchanged.
///
/// Deliberately generic and not Lookup-specific -- a plain, reusable row filter, not a bespoke type
/// per feature.
/// </summary>
public sealed class FilteringRowSource<TRow>(string name, IRowSource<TRow> source, Func<TRow, bool> predicate) : IRowSource<TRow>
{
    public string Name => name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in source.ReadAsync(ct))
        {
            if (predicate(row))
                yield return row;
        }
    }
}
