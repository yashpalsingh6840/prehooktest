using System.Runtime.CompilerServices;
using Etl.Core.Abstractions;

namespace Etl.Core.Data;

/// <summary>
/// A <c>Microsoft.UnionAll</c> component: reads every underlying <see cref="IRowSource{TRow}"/>
/// in order, yielding one side's rows in full before moving to the next -- gap-audit Phase 3.6
/// (2026-09-02). SSIS's own UnionAll requires no sorted input and performs no reordering of its
/// own (confirmed via the component's own official Microsoft-authored description text, extracted
/// directly from the live object model: "Combines rows from multiple data flows WITHOUT SORTING"
/// -- a real dtexec run of the genuinely-multi-independent-source shape this type exists for hit
/// a real, left-unresolved SSIS object-model construction quirk, documented rather than solved in
/// <c>Ssis.Extract.FixtureBuilder.Program.BuildUnionTwoSourcesFixture</c>'s own doc comment; this
/// is the lower-confidence half of gap-audit Phase 3.6, unlike <see cref="MergeInterleaveRowSource{TRow,TKey}"/>'s
/// own dtexec-confirmed algorithm).
///
/// Modeled as a decorator, same precedent as <see cref="SortingRowSource{TRow,TKey}"/>/
/// <see cref="FilteringRowSource{TRow}"/> -- slots into the existing Source-&gt;Transform-&gt;Sink
/// pipeline with zero changes to <c>DataFlowStep&lt;TRow,TEntity&gt;</c>. Genuinely streaming
/// (unlike <see cref="SortingRowSource{TRow,TKey}"/>/<see cref="MergeInterleaveRowSource{TRow,TKey}"/>,
/// which must buffer to sort) -- no reason to buffer a component whose whole job is NOT reordering
/// anything.
/// </summary>
public sealed class ConcatenatingRowSource<TRow>(string name, IReadOnlyList<IRowSource<TRow>> sources) : IRowSource<TRow>
{
    public string Name => name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var source in sources)
        {
            await foreach (var row in source.ReadAsync(ct))
            {
                yield return row;
            }
        }
    }
}
