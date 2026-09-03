using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Covers a <c>Microsoft.UnionAll</c>/<c>Microsoft.Merge</c>'s own PER-INPUT column mapping,
/// which this tool never read at all before the 2026-09-02 gap audit.
///
/// <para>The mapping is explicit, in each input column's <c>OutputColumnLineageID</c>, and it is
/// NOT implied by column names. Measured against real SSIS: a Union All whose output column is
/// named <c>UnifiedName</c> accepted input 1 mapping <c>CustName -&gt; UnifiedName</c> and input 2
/// mapping <c>ClientName -&gt; UnifiedName</c> -- three different names -- and validated
/// VS_ISVALID. So <c>UnionEmitter</c>'s own claim that "real SSIS enforces matching schemas
/// across every Merge/UnionAll input at design time" was false, and its single shared reader,
/// keyed on the union's OUTPUT column names and applied to every side, was only correct by
/// coincidence: a rename made <c>GetOrdinal</c> throw, and a SWAPPED mapping silently transposed
/// that side's values, because every name still resolved.</para>
/// </summary>
public class UnionColumnMappingTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Read_PromotesOutputColumnLineageId_OnAUnionInputColumn()
    {
        var package = LoadSyntheticFixture("SyntheticUnionTwoSources.dtsx");
        var pipeline = package.Executables.Single(e => e.DataFlowTask is not null).DataFlowTask!.Pipeline;
        var union = pipeline.Components.Single(c => c.ComponentClassId == "Microsoft.UnionAll");

        // Every input column must name the output column it feeds. Nothing read this before.
        var columns = union.Inputs.SelectMany(i => i.Columns).ToList();
        Assert.NotEmpty(columns);
        Assert.All(columns, c => Assert.False(string.IsNullOrEmpty(c.OutputColumnLineageId)));
    }

    [Fact]
    public void Plan_ResolvesTheIdentityMapping_ForMatchingColumnNames()
    {
        var package = LoadSyntheticFixture("SyntheticUnionTwoSources.dtsx");

        var plan = PackagePlanner.Plan(package);

        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.Union);
        // Both sides name their columns exactly as the union's output does, so every alias is
        // an identity -- which is why this (already-verified) fixture's generated output is
        // completely unchanged by the mapping work.
        Assert.Equal(2, flow.Union!.Sides.Count);
        Assert.All(flow.Union.Sides, side =>
            Assert.All(side.ColumnAliases, kv => Assert.Equal(kv.Key, kv.Value)));
        Assert.DoesNotContain(plan.Gaps, g => g.Reason.Contains("output column", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_ReportsAGap_WhenAUnionSideMapsAColumnToADifferentlyNamedOutputColumn()
    {
        var package = LoadSyntheticFixture("SyntheticUnionRename.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // The right-hand side exposes ClientName but the union's output column is Name, so the
        // shared reader would look for a name that side does not have. Refused, not guessed.
        var gap = Assert.Single(result.Gaps, g => g.Reason.Contains("ClientName -> Name", StringComparison.Ordinal));
        Assert.True(gap.IsBlocking);
        // Refusing means no runnable program for the flow.
        Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith("Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ReportsAGap_WhenAUnionInputColumnDoesNotResolveToAnOutputColumn()
    {
        // Guards the "refuse to assume same-name mapping" branch: an input column whose
        // OutputColumnLineageID names nothing must gap rather than fall back to matching by name,
        // which is what the tool effectively did for every union before this.
        var package = LoadSyntheticFixture("SyntheticUnionTwoSources.dtsx");
        var pipeline = package.Executables.Single(e => e.DataFlowTask is not null).DataFlowTask!.Pipeline;
        var union = pipeline.Components.Single(c => c.ComponentClassId == "Microsoft.UnionAll");

        // Blank one mapping in the in-memory model -- cheaper and more targeted than a fixture,
        // and it exercises exactly the branch under test.
        var victim = union.Inputs[0].Columns[0];
        union.Inputs[0].Columns[0] = new Model.Pipeline.PipelineInputColumnSpec
        {
            RefId = victim.RefId,
            CachedName = victim.CachedName,
            LineageId = victim.LineageId,
            OutputColumnLineageId = null,
        };

        var plan = PackagePlanner.Plan(package);

        Assert.Contains(plan.Gaps, g =>
            g.Reason.Contains("OutputColumnLineageID", StringComparison.Ordinal) &&
            g.Reason.Contains("refusing to assume", StringComparison.Ordinal));
    }
}
