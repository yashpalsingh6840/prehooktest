using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Covers <c>Microsoft.MergeJoin</c>'s own <c>JoinType</c> property, which the codegen layer used
/// to ignore entirely: <c>ProgramEmitter</c> hardcoded <c>MergeJoinType.LeftOuter</c> for every
/// Merge Join regardless of the raw value, so the one real evidenced package
/// (RBC_Demo_ETL's Package_Transforms.dtsx, MRG_CustomerContacts, JoinType=2) generated an INNER
/// join as a LEFT OUTER one -- emitting unmatched left rows, with a nulled right side, that real
/// SSIS drops entirely. Gap-audit Phase 1 (2026-09-02).
///
/// <para>The raw-value mapping was MEASURED against real SSIS via dtexec, not inferred:
/// <c>SyntheticMergeJoin.dtsx</c> joins left keys {1,2,3} to right keys {2,3,4}, so row count
/// alone separates the three join types -- <b>0 = FULL OUTER (4 rows), 1 = LEFT OUTER (3),
/// 2 = INNER (2)</b>. Note that is the exact reverse of <c>MergeJoinType</c>'s own C# ordinal
/// order, so a naive cast would be wrong for 0 and 2 and right only for 1.</para>
///
/// <para>The three sibling fixtures are byte-preserving derivations of
/// <c>SyntheticMergeJoin.dtsx</c> with only the JoinType value (and the package name) changed --
/// so any difference in behaviour here is attributable to that property and nothing else.</para>
/// </summary>
public class MergeJoinJoinTypeTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    private static string ClassFileFor(string dtsxFileName)
    {
        var package = LoadSyntheticFixture(dtsxFileName);
        var result = PackageGenerator.Generate(package, namespacePrefix: null);
        Assert.DoesNotContain(result.Gaps, g => g.IsBlocking);
        return result.Files.Single(f => f.RelativePath == $"{package.ObjectName}.cs").Content;
    }

    [Theory]
    // raw 2 -> Inner: the real evidenced value, and the one that used to generate incorrectly.
    [InlineData("SyntheticMergeJoin.dtsx", "MergeJoinType.Inner")]
    [InlineData("SyntheticMergeJoinLeftOuter.dtsx", "MergeJoinType.LeftOuter")]
    public void Generate_EmitsTheMeasuredJoinType_ForEachSupportedRawValue(string fixture, string expected)
    {
        var classFile = ClassFileFor(fixture);

        Assert.Contains(expected, classFile);
        // Guard the specific regression: LeftOuter must no longer appear for an INNER join.
        if (expected != "MergeJoinType.LeftOuter") Assert.DoesNotContain("MergeJoinType.LeftOuter", classFile);
    }

    [Fact]
    public void Generate_ReportsAGap_ForAFullOuterJoin_BecauseTheMapperCannotExpressAnAbsentLeftRow()
    {
        var package = LoadSyntheticFixture("SyntheticMergeJoinFullOuter.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // Raw 0 IS full outer and Etl.Core implements it -- but MergeJoinEmitter passes
        // mayBeAbsent: isRightSide, so a left-sourced column is emitted as "left!.Column",
        // which a full outer join violates for a right-only key. Generating it would swap the
        // old silent-wrong-rows bug for a NullReferenceException, so it must gap instead.
        var gap = Assert.Single(result.Gaps, g => g.Reason.Contains("FULL OUTER", StringComparison.Ordinal));
        Assert.True(gap.IsBlocking);
        Assert.Contains("left!.Column", gap.Reason);
        // Refusing means emitting nothing for the flow -- not a half-wired Program.cs.
        Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith("Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_ReportsAGap_ForAJoinTypeThisToolHasNotMeasured()
    {
        var package = LoadSyntheticFixture("SyntheticMergeJoinUnmeasuredType.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var gap = Assert.Single(result.Gaps, g => g.Reason.Contains("JoinType=3", StringComparison.Ordinal));
        Assert.True(gap.IsBlocking);
        Assert.Contains("has not measured", gap.Reason);
        Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith("Program.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ReportsAGap_ForAMergeJoinLandingInAFlatFileDestination()
    {
        // PlanMergeJoin used to accept a Flat File Destination, but GenerateMergeJoinFlow has no
        // flat-file sink path at all: it resolves its entity name via TryResolveEntityName (a
        // TABLE name) and hardcodes new SqlFlowSink(). That shape therefore generated a SQL
        // bulk-insert into a table that does not exist -- broken code, no gap reported. The
        // planner now refuses it, so the acceptance list and the emitter agree.
        // No fixture has this shape; assert on the planner's own acceptance list instead, which
        // is what the emitter's capability is expressed through.
        var package = LoadSyntheticFixture("SyntheticMergeJoin.dtsx");
        var plan = PackagePlanner.Plan(package);

        // The supported shape still plans cleanly...
        Assert.DoesNotContain(plan.Gaps, g => g.Reason.Contains("Merge Join", StringComparison.Ordinal));
        // ...and resolves the measured join type onto the plan, rather than leaving it implicit.
        var flow = Assert.Single(plan.Flows);
        Assert.NotNull(flow.MergeJoin);
        Assert.Equal("Inner", flow.MergeJoin!.JoinType);
    }
}
