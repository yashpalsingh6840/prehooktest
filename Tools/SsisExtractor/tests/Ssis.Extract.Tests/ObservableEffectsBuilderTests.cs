using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Tests;

/// <summary>
/// <see cref="ObservableEffectsBuilder"/> -- built to close a real gap: the gate-3 harness
/// (<c>Validation</c>) only ever compared SQL table rows, so a package whose real
/// output is a file, an email, or anything this extractor has no semantic model for could
/// report PASS having verified nothing. These tests pin the two properties that matter most:
/// a straight-line table-only package reports every effect Verifiable (no false alarms), and
/// a package with a Script Task or Script Component NEVER reports all-Verifiable (no false
/// greens) -- regardless of what future SSIS task type shows up, because the catch-all is an
/// exclusion list of KNOWN-SAFE types, not an allow list of known-dangerous ones.
/// </summary>
public class ObservableEffectsBuilderTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static readonly string PoCPackagesDir = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFilePath())!, "..", "..", "..", "..", "..", "SSIS_Packages", "SSIS"));

    private static readonly string FixturesDir = Path.Combine(Path.GetDirectoryName(ThisFilePath())!, "Fixtures");

    private static List<ObservableEffectSpec> BuildFor(string dtsxPath)
    {
        var package = DtsxPackageReader.Read(dtsxPath, noRedact: false);
        var dataTouch = DataTouchBuilder.Build(package, []);
        return ObservableEffectsBuilder.Build(package, dataTouch);
    }

    [Fact]
    public void LoadEmployees_ReportsExactlyOneVerifiableSqlTableEffect_NoFalseAlarms()
    {
        var effects = BuildFor(Path.Combine(PoCPackagesDir, "LoadEmployees.dtsx"));

        var effect = Assert.Single(effects);
        Assert.Equal("SqlTable", effect.Kind);
        Assert.Equal("dbo.Employee", effect.Target);
        Assert.Equal("Verifiable", effect.Verifiability);
    }

    [Fact]
    public void LoadReferenceData_ReportsBothTargetTables_BothVerifiable_NoFalseAlarms()
    {
        var effects = BuildFor(Path.Combine(PoCPackagesDir, "LoadReferenceData.dtsx"));

        Assert.Equal(2, effects.Count);
        Assert.All(effects, e => Assert.Equal("SqlTable", e.Kind));
        Assert.All(effects, e => Assert.Equal("Verifiable", e.Verifiability));
        Assert.Contains(effects, e => e.Target == "dbo.Department");
        Assert.Contains(effects, e => e.Target == "dbo.Designation");
    }

    [Fact]
    public void PackageWithAScriptTask_NeverReportsFullyVerifiable()
    {
        // The regression this class exists to prevent: a package containing a task type
        // with unknowable side effects must not be able to look fully checked.
        var effects = BuildFor(Path.Combine(FixturesDir, "SyntheticForEachScript.dtsx"));

        Assert.Contains(effects, e => e.Kind == "UncharacterizedTask" && e.Verifiability == "NeedsHumanReview");
        Assert.DoesNotContain(effects, e => e.Verifiability != "Verifiable" && e.Verifiability != "NeedsHumanReview" && e.Verifiability != "NeedsChecker");
        // The whole-package safety property: at least one effect is not Verifiable, so no
        // consumer can treat this package as fully checked by counting rows alone.
        Assert.Contains(effects, e => e.Verifiability != "Verifiable");
    }

    [Fact]
    public void ScriptTaskItself_IsNamedAsTheUncharacterizedEffect()
    {
        var effects = BuildFor(Path.Combine(FixturesDir, "SyntheticForEachScript.dtsx"));

        var scriptEffects = effects.Where(e => e.Kind == "UncharacterizedTask").ToList();
        Assert.Contains(scriptEffects, e => e.Origin == "Microsoft.ScriptTask");
    }

    [Fact]
    public void ForEachLoopContainer_IsNotItselfFlaggedAsUncharacterized()
    {
        // STOCK:FOREACHLOOP is a container with no effect of its own -- only its children's
        // effects matter. Confirmed against the fixture's real XML (DTS:ExecutableType=
        // "STOCK:FOREACHLOOP"), not assumed from a guessed name.
        var effects = BuildFor(Path.Combine(FixturesDir, "SyntheticForEachScript.dtsx"));

        Assert.DoesNotContain(effects, e => e.Origin == "STOCK:FOREACHLOOP");
    }

    [Fact]
    public void PackageWithAScriptComponent_NeverReportsFullyVerifiable()
    {
        // A Script Component lives INSIDE a Microsoft.Pipeline (itself accounted for), so
        // this pins that it is not silently invisible to the catch-all the way a naive
        // "skip anything inside an allowlisted executable" rule would make it.
        var effects = BuildFor(Path.Combine(FixturesDir, "SyntheticScriptComponent.dtsx"));

        Assert.Contains(effects, e => e.Kind == "UncharacterizedTask" && e.Origin.Contains("Script Component"));
    }
}
