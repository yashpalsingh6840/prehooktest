using System.Runtime.CompilerServices;
using Etl.Core.Abstractions;

namespace Etl.Core.Data;

/// <summary>
/// Wraps another <see cref="IRowSource{TRow}"/>, counting every row that passes through and
/// writing the final count into a <see cref="PackageVariables"/> entry once the wrapped source
/// is exhausted -- the read-side translation of SSIS's own <c>Microsoft.RowCount</c> component.
///
/// The count is only meaningful once every row has been read, so it is set exactly once, after
/// the final <c>yield return</c>, never incrementally -- a reader inspecting the variable mid-run
/// would otherwise see a partial value. This mirrors how the SSIS component itself behaves: its
/// own count is only externally visible once its buffer is fully drained (the same "must fully
/// drain before producing a meaningful result" property <see cref="AggregateRowSource{TSourceRow,TKey,TRow}"/>
/// already has on its input side, for the same underlying reason -- a running total isn't the
/// real total until the stream ends).
///
/// Deliberately generic and not RowCount-specific -- a plain, reusable row counter, matching
/// <see cref="FilteringRowSource{TRow}"/>'s own precedent of a thin, reusable decorator rather
/// than a bespoke type per feature.
/// </summary>
public sealed class CountingRowSource<TRow>(string name, IRowSource<TRow> source, PackageVariables variables, string variableName) : IRowSource<TRow>
{
    public string Name => name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var count = 0;
        await foreach (var row in source.ReadAsync(ct))
        {
            count++;
            yield return row;
        }

        variables.Set(variableName, count);
    }
}
