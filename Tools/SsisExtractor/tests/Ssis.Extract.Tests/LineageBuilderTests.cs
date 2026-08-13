using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Tests;

/// <summary>
/// Unit tests for <see cref="LineageBuilder.Build"/> against synthetic pipelines. The two
/// real fixtures (see <c>GoldenFileTests</c>) exercise the common case end to end, but a
/// few interesting shapes aren't present in either PoC package: a column consumed by
/// nothing (<see cref="UnusedColumn_IsFlaggedWhenNoDownstreamConsumer"/>), an expression
/// combining more than two upstream columns, and a dangling lineageId reference. Built
/// directly against the typed model (no XML), same approach as <c>DagAlgorithmTests</c>.
/// </summary>
public class LineageBuilderTests
{
    private static PipelineOutputColumnSpec OutputColumn(string refId, string name, string? expression = null, string? friendlyExpression = null) => new()
    {
        RefId = refId,
        Name = name,
        LineageId = refId,
        Expression = expression,
        FriendlyExpression = friendlyExpression,
    };

    private static PipelineInputColumnSpec InputColumn(string refId, string cachedName, string lineageId) => new()
    {
        RefId = refId,
        CachedName = cachedName,
        LineageId = lineageId,
    };

    [Fact]
    public void PassthroughColumn_ProducesOnePathFlowEdge()
    {
        var pipeline = new PipelineSpec
        {
            Components =
            [
                new PipelineComponentSpec
                {
                    RefId = "Src", Name = "Src", ComponentClassId = "Test.Source",
                    Outputs = [new PipelineOutputSpec { RefId = "Src.Out", Name = "Out", Columns = [OutputColumn("Src.Out.A", "A")] }],
                },
                new PipelineComponentSpec
                {
                    RefId = "Dst", Name = "Dst", ComponentClassId = "Test.Destination",
                    Inputs = [new PipelineInputSpec { RefId = "Dst.In", Name = "In", Columns = [InputColumn("Dst.In.A", "A", "Src.Out.A")] }],
                },
            ],
        };

        var lineage = LineageBuilder.Build(pipeline);

        var edge = Assert.Single(lineage.Edges);
        Assert.Equal("PathFlow", edge.Kind);
        Assert.Equal("Src", edge.FromComponentRefId);
        Assert.Equal("Dst", edge.ToComponentRefId);
        Assert.Null(edge.Expression);
        Assert.Empty(lineage.ConstantColumns);
        Assert.Empty(lineage.UnusedColumns);
    }

    [Fact]
    public void ExpressionWithMultipleReferences_ProducesOneEdgePerReference()
    {
        var pipeline = new PipelineSpec
        {
            Components =
            [
                new PipelineComponentSpec
                {
                    RefId = "Src", Name = "Src", ComponentClassId = "Test.Source",
                    Outputs = [new PipelineOutputSpec
                    {
                        RefId = "Src.Out", Name = "Out",
                        Columns = [OutputColumn("Src.Out.A", "A"), OutputColumn("Src.Out.B", "B"), OutputColumn("Src.Out.C", "C")],
                    }],
                },
                new PipelineComponentSpec
                {
                    RefId = "Xform", Name = "Xform", ComponentClassId = "Test.Transform",
                    Inputs = [new PipelineInputSpec
                    {
                        RefId = "Xform.In", Name = "In",
                        Columns = [InputColumn("Xform.In.A", "A", "Src.Out.A"), InputColumn("Xform.In.B", "B", "Src.Out.B"), InputColumn("Xform.In.C", "C", "Src.Out.C")],
                    }],
                    Outputs = [new PipelineOutputSpec
                    {
                        RefId = "Xform.Out", Name = "Out",
                        Columns = [OutputColumn("Xform.Out.Combined", "Combined",
                            expression: "#{Src.Out.A} + #{Src.Out.B} + #{Src.Out.C}",
                            friendlyExpression: "A + B + C")],
                    }],
                },
            ],
        };

        var lineage = LineageBuilder.Build(pipeline);

        var derived = lineage.Edges.Where(e => e.Kind == "ExpressionDerived").ToList();
        Assert.Equal(3, derived.Count);
        Assert.All(derived, e => Assert.Equal("A + B + C", e.Expression));
        Assert.Equal(["A", "B", "C"], derived.Select(e => e.FromColumnName).OrderBy(x => x));

        // The transform's own inputs are passthrough-consumed too (PathFlow), independent
        // of also feeding the expression -- both relationships are real and both are kept.
        Assert.Equal(3, lineage.Edges.Count(e => e.Kind == "PathFlow"));

        // "Combined" is the pipeline's final output in this synthetic fixture -- nothing
        // downstream consumes it, so it's correctly flagged unused (there's no destination
        // component here to land it, unlike the real PoC fixtures).
        var unused = Assert.Single(lineage.UnusedColumns);
        Assert.Equal("Combined", unused.ColumnName);
    }

    [Fact]
    public void ExpressionWithNoReferences_IsAConstantColumnWithNoIncomingEdge()
    {
        var pipeline = new PipelineSpec
        {
            Components =
            [
                new PipelineComponentSpec
                {
                    RefId = "Xform", Name = "Xform", ComponentClassId = "Test.Transform",
                    Outputs = [new PipelineOutputSpec
                    {
                        RefId = "Xform.Out", Name = "Out",
                        Columns = [OutputColumn("Xform.Out.Now", "Now", expression: "GETUTCDATE()", friendlyExpression: "GETUTCDATE()")],
                    }],
                },
            ],
        };

        var lineage = LineageBuilder.Build(pipeline);

        Assert.Empty(lineage.Edges);
        var constant = Assert.Single(lineage.ConstantColumns);
        Assert.Equal("Now", constant.ColumnName);
        Assert.Equal("GETUTCDATE()", constant.Expression);
        // Produced but never consumed downstream in this synthetic pipeline -- both facts
        // (constant AND unused) can be true of the same column at once, and are.
        Assert.Single(lineage.UnusedColumns);
    }

    [Fact]
    public void UnusedColumn_IsFlaggedWhenNoDownstreamConsumer()
    {
        var pipeline = new PipelineSpec
        {
            Components =
            [
                new PipelineComponentSpec
                {
                    RefId = "Src", Name = "Src", ComponentClassId = "Test.Source",
                    Outputs = [new PipelineOutputSpec
                    {
                        RefId = "Src.Out", Name = "Out",
                        Columns = [OutputColumn("Src.Out.A", "A"), OutputColumn("Src.Out.Unread", "Unread")],
                    }],
                },
                new PipelineComponentSpec
                {
                    RefId = "Dst", Name = "Dst", ComponentClassId = "Test.Destination",
                    Inputs = [new PipelineInputSpec { RefId = "Dst.In", Name = "In", Columns = [InputColumn("Dst.In.A", "A", "Src.Out.A")] }],
                },
            ],
        };

        var lineage = LineageBuilder.Build(pipeline);

        var unused = Assert.Single(lineage.UnusedColumns);
        Assert.Equal("Unread", unused.ColumnName);
        Assert.Equal("Src", unused.ComponentRefId);
    }

    [Fact]
    public void DanglingLineageId_ProducesAnUnresolvedEdgeInsteadOfCrashing()
    {
        var pipeline = new PipelineSpec
        {
            Components =
            [
                new PipelineComponentSpec
                {
                    RefId = "Dst", Name = "Dst", ComponentClassId = "Test.Destination",
                    Inputs = [new PipelineInputSpec { RefId = "Dst.In", Name = "In", Columns = [InputColumn("Dst.In.Ghost", "Ghost", "NoSuchProducer")] }],
                },
            ],
        };

        var lineage = LineageBuilder.Build(pipeline);

        var edge = Assert.Single(lineage.Edges);
        Assert.Equal("", edge.FromComponentRefId);
        Assert.Equal("(unresolved)", edge.FromComponentName);
        Assert.Equal("NoSuchProducer", edge.FromColumnName);
    }

    [Fact]
    public void EmptyPipeline_ReturnsEmptyLineageWithoutError()
    {
        var lineage = LineageBuilder.Build(new PipelineSpec());

        Assert.Empty(lineage.Edges);
        Assert.Empty(lineage.ConstantColumns);
        Assert.Empty(lineage.UnusedColumns);
    }
}
