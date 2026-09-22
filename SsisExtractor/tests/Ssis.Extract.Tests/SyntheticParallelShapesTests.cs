using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Tests;

/// <summary>
/// Assertion-based tests (not golden-file: this fixture is expected to evolve) against
/// <c>SyntheticParallelShapes.dtsx</c> -- built via the real SSIS 17 object model
/// (<c>src/Ssis.Extract.FixtureBuilder/</c>) to exercise three shapes neither PoC package
/// nor any of the three earlier synthetic fixtures ever exercised:
/// <list type="bullet">
/// <item>real parallel control flow -- every prior fixture is a straight-line chain, so
/// <see cref="Model.Package.ControlFlowDagSpec.ParallelLevels"/> had never been exercised
/// against more than one node per level;</item>
/// <item>an OLE DB Source -> Destination direct copy with no transform in between, twice
/// in one Data Flow Task;</item>
/// <item>a Flat File Source's error output routed through a Script Component into its own
/// destination -- error-row redirection, completely unmodeled before this fixture.</item>
/// </list>
/// See Tools/SsisExtractor/docs/report-schema.md "Synthetic parallel-shapes fixture" for the
/// build technique and the real gotchas hit constructing it (a FlatFileColumn's Name has no
/// property on its own COM interface -- only via an <c>IDTSName100</c> cast; a Script
/// Component's script-specific custom properties only appear if
/// <c>UserComponentTypeName</c> is seeded BEFORE <c>ProvideComponentProperties()</c>, not
/// after; a Flat File Source's error output has a FIXED three-column shape unrelated to the
/// source's own columns).
///
/// <para>The <c>.dtsx</c> itself lives under <c>SSIS/</c> alongside the two real PoC
/// packages (and registered in <c>SSIS.dtproj</c>), not under this test project's own
/// <c>Fixtures/</c> folder like the three earlier synthetic fixtures -- moved there
/// deliberately so it is visible in SSDT's Package Explorer / Solution Explorer the same way
/// LoadEmployees/LoadReferenceData are. It is still not part of the PoC's own deliverable:
/// referenced here by relative path, same as <c>ConformanceTests.PoCPackagesDir</c> reads the
/// real packages, rather than copied.</para>
/// </summary>
public class SyntheticParallelShapesTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    // tests/Ssis.Extract.Tests -> Tools/SsisExtractor -> repo root -> SSIS/
    private static readonly string SsisProjectDir = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFilePath())!, "..", "..", "..", "..", "..", "SSIS_Packages", "SSIS"));

    private static string FixturePath => Path.Combine(SsisProjectDir, "SyntheticParallelShapes.dtsx");

    private static PackageSpec Read() => DtsxPackageReader.Read(FixturePath, noRedact: false);

    [Fact]
    public void ExtractsAt100PercentCoverage()
    {
        Assert.Equal(100.0, Read().Coverage.CoveragePercent);
    }

    [Fact]
    public void RootDag_HasThreeIndependentTopLevelBranches_NoCycle()
    {
        // The whole point of this fixture: SQL_CreateLoadATemp, DFT_DirectCopy, and
        // DFT_ErrorRouting carry NO precedence constraints between them, so all three must
        // land in the SAME parallel level -- the first real multi-node level any fixture in
        // this repo has ever produced (every prior package is a straight-line chain).
        var dag = Read().Dag;

        Assert.False(dag.HasCycle);
        Assert.NotEmpty(dag.ParallelLevels);

        var firstLevel = dag.ParallelLevels[0].ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, firstLevel.Count);
        Assert.Contains("Package\\SQL_CreateLoadATemp", firstLevel);
        Assert.Contains("Package\\DFT_DirectCopy", firstLevel);
        Assert.Contains("Package\\DFT_ErrorRouting", firstLevel);

        // Branch 1's chain: SQL_CreateLoadATemp -> DFT_LoadA -> SQL_UpdateLoadA, so the
        // remaining two levels are singletons, downstream of the first level only through
        // that one branch -- exactly the "independent chains of different lengths" case
        // ControlFlowDagSpec.ParallelLevels' own doc comment names as a documented limit.
        Assert.Equal(["Package\\DFT_LoadA"], dag.ParallelLevels[1]);
        Assert.Equal(["Package\\SQL_UpdateLoadA"], dag.ParallelLevels[2]);
    }

    [Fact]
    public void Branch1_ExecuteSqlRunsAfterTheDataFlow_NotOnlyBefore()
    {
        // The generated C# rewrite's control-flow handling only modeled PRE-load SQL at the
        // time this fixture was built. This is the concrete evidence that a real package can
        // need SQL sequenced AFTER a data flow too -- SQL_UpdateLoadA has an incoming
        // constraint FROM DFT_LoadA.
        var package = Read();
        var constraint = Assert.Single(package.PrecedenceConstraints, c => c.To == "Package\\SQL_UpdateLoadA");
        Assert.Equal("Package\\DFT_LoadA", constraint.From);
    }

    [Fact]
    public void Branch2_HasTwoIndependentDirectCopyPipelines_NoTransformBetweenThem()
    {
        var package = Read();
        var dft = PackageTree.AllExecutables(package).Single(e => e.ObjectName == "DFT_DirectCopy");
        var pipeline = dft.DataFlowTask!.Pipeline!;

        Assert.Equal(4, pipeline.Components.Count);
        var componentClassIds = pipeline.Components.Select(c => c.ComponentClassId).ToList();
        Assert.Equal(2, componentClassIds.Count(id => id == "Microsoft.OLEDBSource"));
        Assert.Equal(2, componentClassIds.Count(id => id == "Microsoft.OLEDBDestination"));

        // No Derived Column, no expression anywhere in this branch -- the direct-copy shape
        // Etl.Core has no abstraction for, per the generator plan's own scope table.
        Assert.DoesNotContain(pipeline.Components, c => c.ComponentClassId == "Microsoft.DerivedColumn");
    }

    [Fact]
    public void Branch3_FlatFileSource_HasBothAMainAndAnErrorOutput()
    {
        var package = Read();
        var dft = PackageTree.AllExecutables(package).Single(e => e.ObjectName == "DFT_ErrorRouting");
        var flatFileSource = dft.DataFlowTask!.Pipeline!.Components.Single(c => c.ComponentClassId == "Microsoft.FlatFileSource");

        Assert.Equal(2, flatFileSource.Outputs.Count);
        var errorOutput = Assert.Single(flatFileSource.Outputs, o => o.Name == "Flat File Source Error Output");

        // The error output's shape is fixed by SSIS itself, unrelated to the source's own
        // column list -- confirmed empirically while building this fixture (a first attempt
        // sized the destination table after the MAIN output's columns and failed to map).
        Assert.Equal(
            ["ErrorCode", "ErrorColumn", "Flat File Source Error Output Column"],
            errorOutput.Columns.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Branch3_ErrorOutput_IsRoutedThroughAScriptComponent_IntoItsOwnDestination()
    {
        var package = Read();
        var dft = PackageTree.AllExecutables(package).Single(e => e.ObjectName == "DFT_ErrorRouting");
        var pipeline = dft.DataFlowTask!.Pipeline!;

        var scriptComponent = pipeline.Components.Single(c => c.ComponentClassId == "Microsoft.ManagedComponentHost");
        Assert.NotNull(scriptComponent.ScriptComponent);
        Assert.Equal("CSharp", scriptComponent.ScriptComponent!.Language);
        Assert.False(scriptComponent.ScriptComponent.SourceStripped);
        Assert.NotEmpty(scriptComponent.ScriptComponent.SourceCodeItems);

        // The physical wiring: error output -> Script Component -> its own OLE DB Destination.
        var errorPath = Assert.Single(pipeline.Paths, p => p.Name == "Flat File Source Error Output");
        Assert.Equal(scriptComponent.Inputs[0].RefId, errorPath.EndId);

        var destinations = pipeline.Components.Where(c => c.ComponentClassId == "Microsoft.OLEDBDestination").ToList();
        Assert.Equal(2, destinations.Count);
        Assert.Contains(pipeline.Paths, p => p.StartId == scriptComponent.Outputs[0].RefId
            && destinations.Any(d => d.Inputs.Any(i => i.RefId == p.EndId)));
    }

    [Fact]
    public void ScriptComponentInErrorPath_IsReportedAsAnUncharacterizedEffect_NeedsHumanReview()
    {
        // Ties this fixture back to the false-green fix: a Script Component's transform body
        // can have any side effect at all, so it must never look "fully verified" just
        // because the table effects around it were.
        var package = Read();
        var dataTouch = DataTouchBuilder.Build(package, []);
        var effects = ObservableEffectsBuilder.Build(package, dataTouch);

        var scriptEffect = Assert.Single(effects, e => e.Kind == "UncharacterizedTask");
        Assert.Equal("NeedsHumanReview", scriptEffect.Verifiability);
        Assert.Contains("Script Component", scriptEffect.Origin);
    }

    [Fact]
    public void AllFiveDestinationTables_AreReportedAsVerifiableSqlTableEffects()
    {
        var package = Read();
        var dataTouch = DataTouchBuilder.Build(package, []);
        var effects = ObservableEffectsBuilder.Build(package, dataTouch);

        var tableEffects = effects.Where(e => e.Kind == "SqlTable").ToList();
        Assert.Equal(
            ["dbo.SyntheticDirectACopy", "dbo.SyntheticDirectBCopy", "dbo.SyntheticErrorRoutingCaught",
             "dbo.SyntheticErrorRoutingMain", "dbo.SyntheticLoadATemp"],
            tableEffects.Select(e => e.Target).OrderBy(t => t, StringComparer.Ordinal));
        Assert.All(tableEffects, e => Assert.Equal("Verifiable", e.Verifiability));
    }
}
