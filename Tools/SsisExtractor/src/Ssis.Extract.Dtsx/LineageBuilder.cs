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
