using System.Text.RegularExpressions;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Derives column-level lineage (plan §5.1) from an already-parsed <see cref="PipelineSpec"/>.
/// Pure/offline -- no XML here, only the typed model -- so it's unit-testable against
/// synthetic pipelines the same way <c>DtsxPackageReader.BuildDag</c> is (see
/// <c>LineageBuilderTests</c>), independent of what either PoC package happens to contain.
/// See <see cref="LineageSpec"/>'s doc comment for why this matches by <c>lineageId</c>
/// rather than walking <see cref="PipelineSpec.Paths"/>.
/// </summary>
public static partial class LineageBuilder
{
    private readonly record struct ColumnRef(string ComponentRefId, string ComponentName, string ColumnRefId, string ColumnName, string LineageId);

    public static LineageSpec Build(PipelineSpec pipeline)
    {
        var producers = new Dictionary<string, ColumnRef>();
        foreach (var comp in pipeline.Components)
        {
            foreach (var output in comp.Outputs)
            {
                foreach (var col in output.Columns)
                {
                    // A lineageId is only ever assigned by the one output column that
                    // creates it, so first-write-wins is just defensive -- a duplicate
                    // would mean a malformed package, not a real ambiguity to resolve.
                    producers.TryAdd(col.LineageId, new ColumnRef(comp.RefId, comp.Name, col.RefId, col.Name, col.LineageId));
                }
            }
        }

        var edges = new List<LineageEdgeSpec>();
        var consumedLineageIds = new HashSet<string>();

        foreach (var comp in pipeline.Components)
        {
            foreach (var input in comp.Inputs)
            {
                foreach (var col in input.Columns)
                {
                    consumedLineageIds.Add(col.LineageId);
                    var to = new ColumnRef(comp.RefId, comp.Name, col.RefId, col.CachedName, col.LineageId);
                    edges.Add(MakeEdge(producers, col.LineageId, to, kind: "PathFlow", expression: null));

                    // An IN-PLACE modification (Derived Column's "Replace <column>" mode) keeps
                    // the upstream lineageId and declares no output column, so the PathFlow edge
                    // above is the only thing that used to exist for it -- and PathFlow says
                    // "this value arrived here", not "this value was rewritten here". Emit the
                    // same ExpressionDerived edges the output-column walk below emits, so every
                    // lineage consumer sees the transformation. Without this a replaced column
                    // is indistinguishable from a plain passthrough.
                    if (col.Expression is null) continue;
                    var inPlaceRefs = LineageRefPattern().Matches(col.Expression).Select(m => m.Groups[1].Value).ToList();
                    if (inPlaceRefs.Count == 0) continue;

                    var inPlaceText = col.FriendlyExpression ?? col.Expression;
                    foreach (var refLineageId in inPlaceRefs)
                    {
                        consumedLineageIds.Add(refLineageId);
                        edges.Add(MakeEdge(producers, refLineageId, to, kind: "ExpressionDerived", expression: inPlaceText));
                    }
                }
            }

            foreach (var output in comp.Outputs)
            {
                foreach (var col in output.Columns)
                {
                    if (col.Expression is null) continue;
                    var referenced = LineageRefPattern().Matches(col.Expression).Select(m => m.Groups[1].Value).ToList();
                    if (referenced.Count == 0) continue; // no #{...} refs -- see ConstantColumns below, not an edge

                    var to = new ColumnRef(comp.RefId, comp.Name, col.RefId, col.Name, col.LineageId);
                    var expressionText = col.FriendlyExpression ?? col.Expression;
                    foreach (var refLineageId in referenced)
                    {
                        consumedLineageIds.Add(refLineageId);
                        edges.Add(MakeEdge(producers, refLineageId, to, kind: "ExpressionDerived", expression: expressionText));
                    }
                }
            }

            // A Data Conversion output column has no Expression at all -- its whole "how was
            // this produced" story is the single SourceColumnLineageId reference promoted onto
            // DataConvertPayload (see that type's own doc comment for why Expression-based
            // parsing above never sees it). Without this, a converted column would be neither
            // an edge nor a ConstantColumns entry -- invisible to every lineage-based consumer
            // (TransformEmitter's reference resolution included), the exact "row-type
            // generation" gap CLAUDE.md names for this component.
            if (comp.DataConvert is { } dataConvert)
            {
                foreach (var dcol in dataConvert.Columns)
                {
                    if (dcol.SourceColumnLineageId is not { } refLineageId) continue;
                    var outputCol = comp.Outputs
                        .SelectMany(o => o.Columns)
                        .FirstOrDefault(c => c.Name == dcol.OutputColumnName);
                    if (outputCol is null) continue;

                    var to = new ColumnRef(comp.RefId, comp.Name, outputCol.RefId, outputCol.Name, outputCol.LineageId);
                    consumedLineageIds.Add(refLineageId);
                    edges.Add(MakeEdge(producers, refLineageId, to, kind: "DataConversion", expression: null));
                }
            }

            // A Copy Column (Microsoft.CopyMap) output column has no Expression either -- same
            // reasoning as Data Conversion above, just a different property name (copyColumnId,
            // promoted onto CopyMapPayload -- see that type's own doc comment). Added for Phase 1
            // of the unsupported-component-types plan.
            if (comp.CopyMap is { } copyMap)
            {
                foreach (var ccol in copyMap.Columns)
                {
                    if (ccol.SourceColumnLineageId is not { } refLineageId) continue;
                    var outputCol = comp.Outputs
                        .SelectMany(o => o.Columns)
                        .FirstOrDefault(c => c.Name == ccol.OutputColumnName);
                    if (outputCol is null) continue;

                    var to = new ColumnRef(comp.RefId, comp.Name, outputCol.RefId, outputCol.Name, outputCol.LineageId);
                    consumedLineageIds.Add(refLineageId);
                    edges.Add(MakeEdge(producers, refLineageId, to, kind: "CopyMap", expression: null));
                }
            }

            // An Aggregate output column (GroupBy key or Count) has no Expression either --
            // same reasoning as Data Conversion above, just a different payload/property name
            // (AggregationColumnId, promoted onto AggregatePayload -- see that type's own doc
            // comment). Built speculatively 2026-08-30.
            if (comp.Aggregate is { } aggregate)
            {
                foreach (var acol in aggregate.Columns)
                {
                    if (acol.SourceColumnLineageId is not { } refLineageId) continue;
                    var outputCol = comp.Outputs
                        .SelectMany(o => o.Columns)
                        .FirstOrDefault(c => c.Name == acol.OutputColumnName);
                    if (outputCol is null) continue;

                    var to = new ColumnRef(comp.RefId, comp.Name, outputCol.RefId, outputCol.Name, outputCol.LineageId);
                    consumedLineageIds.Add(refLineageId);
                    edges.Add(MakeEdge(producers, refLineageId, to, kind: "Aggregate", expression: null));
                }
            }
        }

        var constantColumns = new List<LineageColumnRefSpec>();
        var unusedColumns = new List<LineageColumnRefSpec>();
        foreach (var comp in pipeline.Components)
        {
            foreach (var output in comp.Outputs)
            {
                foreach (var col in output.Columns)
                {
                    if (col.Expression is not null && LineageRefPattern().Matches(col.Expression).Count == 0)
                    {
                        constantColumns.Add(new LineageColumnRefSpec
                        {
                            ComponentRefId = comp.RefId,
                            ComponentName = comp.Name,
                            ColumnRefId = col.RefId,
                            ColumnName = col.Name,
                            LineageId = col.LineageId,
                            Expression = col.FriendlyExpression ?? col.Expression,
                        });
                    }

                    if (!consumedLineageIds.Contains(col.LineageId))
                    {
                        unusedColumns.Add(new LineageColumnRefSpec
                        {
                            ComponentRefId = comp.RefId,
                            ComponentName = comp.Name,
                            ColumnRefId = col.RefId,
                            ColumnName = col.Name,
                            LineageId = col.LineageId,
                        });
                    }
                }
            }
        }

        return new LineageSpec { Edges = edges, ConstantColumns = constantColumns, UnusedColumns = unusedColumns };
    }

    private static LineageEdgeSpec MakeEdge(Dictionary<string, ColumnRef> producers, string lineageId, ColumnRef to, string kind, string? expression)
    {
        // A lineageId with no known producer shouldn't happen in a well-formed pipeline,
        // but (same resilience philosophy as BuildDag's dangling-reference handling) a
        // hand-edited or partially-modeled package shouldn't crash the whole extraction --
        // surface it loudly as an unresolved edge instead.
        var from = producers.TryGetValue(lineageId, out var p)
            ? p
            : new ColumnRef("", "(unresolved)", "", lineageId, lineageId);

        return new LineageEdgeSpec
        {
            FromComponentRefId = from.ComponentRefId,
            FromComponentName = from.ComponentName,
            FromColumnRefId = from.ColumnRefId,
            FromColumnName = from.ColumnName,
            LineageId = lineageId,
            ToComponentRefId = to.ComponentRefId,
            ToComponentName = to.ComponentName,
            ToColumnRefId = to.ColumnRefId,
            ToColumnName = to.ColumnName,
            Kind = kind,
            Expression = expression,
        };
    }

    [GeneratedRegex(@"#\{([^}]+)\}")]
    private static partial Regex LineageRefPattern();
}
