using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// SSIS runs executables with no ordering constraint between them concurrently; the generated
/// PackageRunner runs every step in sequence. The data is identical either way, so this is an
/// advisory (non-blocking) gap -- but it has to be REPORTED, because saying nothing about it is
/// what actually misleads: a package whose four branches ran in parallel generates code that
/// looks complete and takes four times as long.
///
/// The negative case below matters more than the positive one. A false positive here would
/// attach a permanent advisory to every strictly-sequential package in a client portfolio, which
/// is exactly the noise that trains people to ignore the gap list.
/// </summary>
public class ParallelismAdvisoryTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Plan_ReportsAParallelismAdvisory_ForPrecedenceIndependentRootExecutables()
    {
        // Three root Data Flow Tasks, zero precedence constraints between them.
        var package = LoadSyntheticFixture("SyntheticFlatFileDestination.dtsx");

        var plan = PackagePlanner.Plan(package);

        var gap = Assert.Single(plan.Gaps, g => g.Location.EndsWith(".Parallelism"));
        Assert.Equal("SyntheticFlatFileDestination.Parallelism", gap.Location);

        // Non-blocking, and NOT routed to an AI work packet: this is a missing runtime capability,
        // not a missing datum or a script to port.
        Assert.False(gap.IsBlocking);
        Assert.Equal(GapKind.Unclassified, gap.Kind);
        Assert.Equal(GapTier.Advisory, GapIdentity.TierOf(gap));
        Assert.False(GapIdentity.HasWorkPacket(gap));

        // The evidence is what makes this actionable rather than a warning to scroll past: how
        // many executables were concurrent, and what the package itself declared.
        Assert.Contains("up to 3 executables", gap.Reason);
        Assert.Contains("MaxConcurrentExecutables not declared", gap.Reason);
        Assert.Contains("logical processors + 2", gap.Reason);
    }

    [Theory]
    // SQL_PreLoad -> DFT_Load -> SQL_PostLoad: a strict chain.
    [InlineData("SyntheticPostFlowSql.dtsx")]
    // A Sequence Container (Execute SQL -> Data Flow) followed by a root-level Data Flow Task --
    // fully constrained at both levels, so neither the root nor the nested container is parallel.
    [InlineData("SyntheticNestedContainer.dtsx")]
    // Both real PoC packages are straight A -> B / A -> B -> C chains.
    [InlineData("../../../../../SSIS/LoadEmployees.dtsx")]
    public void Plan_ReportsNoParallelismAdvisory_ForAStrictlySequentialPackage(string fixture)
    {
        var package = LoadSyntheticFixture(fixture);

        var plan = PackagePlanner.Plan(package);

        Assert.DoesNotContain(plan.Gaps, g => g.Location.EndsWith(".Parallelism"));
    }
}
