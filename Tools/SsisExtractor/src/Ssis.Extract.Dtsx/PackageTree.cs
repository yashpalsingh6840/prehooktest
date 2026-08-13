using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Dtsx;

/// <summary>Shared control-flow tree walk, used by every derived-analysis pass (<see cref="ComplexityScorer"/>, <c>RulesEngine</c>, <c>NonDeterminismAnalyzer</c>) that needs "every executable in this package" rather than just the top level.</summary>
public static class PackageTree
{
    /// <summary>Every <see cref="ExecutableSpec"/> at any depth: the package root's own tree (recursively through <see cref="ExecutableSpec.Children"/>) plus every event handler's own tree (event handlers share the same container shape -- see <c>DtsxPackageReader.ReadEventHandlers</c>). Neither PoC package has an event handler, so this is equivalent to just walking <see cref="PackageSpec.Executables"/> for both fixtures today, but a client package might.</summary>
    public static IEnumerable<ExecutableSpec> AllExecutables(PackageSpec package)
    {
        foreach (var ex in WalkExecutables(package.Executables))
        {
            yield return ex;
        }
        foreach (var eh in package.EventHandlers)
        {
            foreach (var ex in WalkExecutables(eh.Children))
            {
                yield return ex;
            }
        }
    }

    private static IEnumerable<ExecutableSpec> WalkExecutables(List<ExecutableSpec> executables)
    {
        foreach (var ex in executables)
        {
            yield return ex;
            foreach (var child in WalkExecutables(ex.Children))
            {
                yield return child;
            }
            foreach (var eh in ex.EventHandlers)
            {
                foreach (var child in WalkExecutables(eh.Children))
                {
                    yield return child;
                }
            }
        }
    }
}
