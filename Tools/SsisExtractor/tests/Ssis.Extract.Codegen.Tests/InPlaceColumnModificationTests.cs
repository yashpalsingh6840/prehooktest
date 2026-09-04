using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Covers IN-PLACE COLUMN MODIFICATION, the structural blind spot found by the 2026-09-02 gap
/// audit. A component that REWRITES an existing column rather than adding a new one persists the
/// rewrite on its own INPUT column (<c>usageType="readWrite"</c>) and declares NO output column,
/// and the column keeps its UPSTREAM lineageId. Every output-column-based mechanism in this tool
/// was therefore blind to it at once -- codegen emitted a plain passthrough of the RAW value,
/// while conformance (gate 1), testgen (gate 2), expressions.csv and the non-determinism manifest
/// all recorded nothing -- and it did so reporting 0 blocking gaps and 100% coverage.
///
/// <para>Two fixtures, deliberately the same structural shape with opposite expected outcomes:
/// Derived Column "Replace &lt;column&gt;" mode is TRANSLATED (its expression language is already
/// oracle-verified), while <c>Microsoft.CharacterMap</c> is GAPPED (its ~20 MapFlags values have
/// never been measured, and inferring "8 means uppercase" from the flag name is exactly the
/// unmeasured guess that produced the Merge Join JoinType bug).</para>
/// </summary>
public class InPlaceColumnModificationTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Read_PromotesTheExpressionOffAReadWriteInputColumn()
    {
        var package = LoadSyntheticFixture("SyntheticDerivedColumnReplace.dtsx");

        var pipeline = package.Executables.Single(e => e.DataFlowTask is not null).DataFlowTask!.Pipeline;
        var derivedColumn = pipeline.Components.Single(c => c.ComponentClassId == "Microsoft.DerivedColumn");

        // The rewrite lives HERE, on the input column -- not on any output column.
        var inPlace = derivedColumn.Inputs.SelectMany(i => i.Columns).Single(c => c.CachedName == "Name");
        Assert.Equal("readWrite", inPlace.UsageType);
        Assert.Equal("UPPER(Name)", inPlace.FriendlyExpression);
        Assert.NotNull(inPlace.Expression);

        // The passthrough column alongside it is untouched, so "readWrite" genuinely
        // discriminates rather than being set on everything.
        var passthrough = derivedColumn.Inputs.SelectMany(i => i.Columns).Single(c => c.CachedName == "ID");
        Assert.Null(passthrough.Expression);
        Assert.NotEqual("readWrite", passthrough.UsageType);

        // And the component declares NO non-error output column at all -- which is precisely why
        // every outputs-only walk saw nothing.
        Assert.Empty(derivedColumn.Outputs.Where(o => o.IsErrorOut != true).SelectMany(o => o.Columns));
    }

    [Fact]
    public void Build_EmitsAnExpressionDerivedEdge_ForAnInPlaceColumn()
    {
        var package = LoadSyntheticFixture("SyntheticDerivedColumnReplace.dtsx");
        var pipeline = package.Executables.Single(e => e.DataFlowTask is not null).DataFlowTask!.Pipeline;

        var lineage = LineageBuilder.Build(pipeline);

        // Before the fix the ONLY edge into this column was PathFlow ("the value arrived here"),
        // which is indistinguishable from an untouched passthrough.
        var edge = Assert.Single(lineage.Edges.Where(e =>
            e.Kind == "ExpressionDerived" && e.ToColumnName == "Name"));
        Assert.Equal("UPPER(Name)", edge.Expression);
    }

    [Fact]
    public void Harvest_IncludesAnInPlaceExpression()
    {
        var package = LoadSyntheticFixture("SyntheticDerivedColumnReplace.dtsx");

        var harvested = ExpressionHarvester.Harvest(package);

        // Feeds conformance (gate 1), testgen (gate 2), expressions.csv and the non-determinism
        // manifest -- all four were silently blank for this shape.
        var expr = Assert.Single(harvested.Where(h => h.Kind == "DerivedColumn"));
        Assert.Equal("UPPER(Name)", expr.FriendlyExpression);
        Assert.Contains("UPPER", expr.Functions);
    }

    [Fact]
    public void Generate_TranslatesAnInPlaceDerivedColumn_InsteadOfPassingTheRawValueThrough()
    {
        var package = LoadSyntheticFixture("SyntheticDerivedColumnReplace.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);
        var transform = result.Files.Single(f => f.RelativePath.EndsWith("Transform.cs", StringComparison.Ordinal)).Content;
        // This is the whole bug in one line: it used to read "Name = row.Name,". Now it's its own
        // named, callable function -- not inlined -- but the underlying computation is the same.
        Assert.Contains("Name = ComputeName(row, ctx),", transform);
        Assert.Contains("=> SsisFn.Upper(row.Name);", transform);
        Assert.DoesNotContain("Name = row.Name,", transform);
        // The genuine passthrough beside it must still be a passthrough.
        Assert.Contains("ID = row.ID,", transform);
    }

    [Fact]
    public void Generate_ReportsAGap_ForAnInPlaceCharacterMap_RatherThanGuessingMapFlags()
    {
        var package = LoadSyntheticFixture("SyntheticCharacterMap.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // Structurally identical to the Derived Column fixture, but nothing about MapFlags has
        // ever been measured, so the honest outcome is a named gap on the exact column.
        var gap = Assert.Single(result.Gaps, g => g.Reason.Contains("Microsoft.CharacterMap", StringComparison.Ordinal));
        Assert.True(gap.IsBlocking);
        Assert.Contains("Name", gap.Reason);

        // Whatever else is generated, it must not silently claim to carry that column.
        var transform = result.Files.FirstOrDefault(f => f.RelativePath.EndsWith("Transform.cs", StringComparison.Ordinal));
        if (transform is not null) Assert.DoesNotContain("Name = row.Name,", transform.Content);
    }
}
