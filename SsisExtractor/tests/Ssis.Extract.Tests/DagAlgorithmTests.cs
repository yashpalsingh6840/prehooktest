using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Tests;

/// <summary>
/// Unit tests for <see cref="DtsxPackageReader.BuildDag"/> against synthetic graphs.
/// Neither of this PoC's two real fixtures branches -- both are straight-line chains
/// (see <c>GoldenFileTests</c>) -- so the interesting cases (parallel levels, a cycle, a
/// dangling reference) can only be exercised with fabricated data. Exposed via
/// <c>InternalsVisibleTo</c> rather than duplicating the algorithm here or fabricating a
/// full .dtsx just to reach it.
/// </summary>
public class DagAlgorithmTests
{
    private static PrecedenceConstraintSpec Edge(string from, string to) => new() { From = from, To = to };

    [Fact]
    public void Diamond_PutsIndependentRootsAndIndependentMidTierInTheSameLevel()
    {
        // A   B       (level 0 -- no incoming edge)
        // |   |
        // C   D       (level 1 -- C depends only on A, D depends only on B; C and D have
        //             no path between them in either direction, same as A and B)
        //  \ /
        //   E         (level 2 -- depends on both C and D)
        var nodes = new List<string> { "A", "B", "C", "D", "E" };
        var edges = new List<PrecedenceConstraintSpec>
        {
            Edge("A", "C"),
            Edge("B", "D"),
            Edge("C", "E"),
            Edge("D", "E"),
        };

        var dag = DtsxPackageReader.BuildDag(nodes, edges);

        Assert.False(dag.HasCycle);
        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, dag.TopologicalOrder);
        Assert.Equal(3, dag.ParallelLevels.Count);
        Assert.Equal(new[] { "A", "B" }, dag.ParallelLevels[0].OrderBy(x => x));
        Assert.Equal(new[] { "C", "D" }, dag.ParallelLevels[1].OrderBy(x => x));
        Assert.Equal(new[] { "E" }, dag.ParallelLevels[2]);
    }

    [Fact]
    public void StraightLineChain_EveryNodeGetsItsOwnLevel()
    {
        // Matches the shape of both this PoC's actual packages: a strict A -> B -> C
        // chain has no parallelism opportunity at all -- every level is a singleton.
        var nodes = new List<string> { "A", "B", "C" };
        var edges = new List<PrecedenceConstraintSpec> { Edge("A", "B"), Edge("B", "C") };

        var dag = DtsxPackageReader.BuildDag(nodes, edges);

        Assert.False(dag.HasCycle);
        Assert.Equal(new[] { "A", "B", "C" }, dag.TopologicalOrder);
        Assert.Equal(
        [
            new List<string> { "A" },
            new List<string> { "B" },
            new List<string> { "C" },
        ], dag.ParallelLevels);
    }

    [Fact]
    public void NoConstraints_EveryNodeIsRootLevelAndMutuallyParallel()
    {
        var nodes = new List<string> { "A", "B", "C" };

        var dag = DtsxPackageReader.BuildDag(nodes, []);

        Assert.False(dag.HasCycle);
        Assert.Equal(3, dag.TopologicalOrder.Count); // some order; all are roots, nothing to violate
        Assert.Single(dag.ParallelLevels);
        Assert.Equal(new[] { "A", "B", "C" }, dag.ParallelLevels[0].OrderBy(x => x));
    }

    [Fact]
    public void Cycle_IsDetectedAndReportedRatherThanLoopingOrThrowing()
    {
        var nodes = new List<string> { "A", "B" };
        var edges = new List<PrecedenceConstraintSpec> { Edge("A", "B"), Edge("B", "A") };

        var dag = DtsxPackageReader.BuildDag(nodes, edges);

        Assert.True(dag.HasCycle);
        Assert.Empty(dag.TopologicalOrder);
        Assert.Empty(dag.ParallelLevels);
    }

    [Fact]
    public void DanglingReference_IsExcludedRatherThanCrashing()
    {
        // A constraint referencing a refId outside this container's own children --
        // shouldn't occur in a well-formed .dtsx, but a hand-edited/corrupted one
        // shouldn't take down the whole extraction over it either.
        var nodes = new List<string> { "A", "B" };
        var edges = new List<PrecedenceConstraintSpec> { Edge("A", "B"), Edge("Ghost", "B") };

        var dag = DtsxPackageReader.BuildDag(nodes, edges);

        Assert.False(dag.HasCycle);
        Assert.Equal(new[] { "A", "B" }, dag.TopologicalOrder);
    }

    [Fact]
    public void EmptyContainer_ReturnsEmptyDagWithoutError()
    {
        var dag = DtsxPackageReader.BuildDag([], []);

        Assert.False(dag.HasCycle);
        Assert.Empty(dag.TopologicalOrder);
        Assert.Empty(dag.ParallelLevels);
    }
}
