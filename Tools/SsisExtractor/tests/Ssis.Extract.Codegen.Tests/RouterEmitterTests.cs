using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

public class RouterEmitterTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    private static ConditionalSplitPlan LoadConditionalSplitPlan()
    {
        var package = LoadSyntheticFixture("SyntheticConditionalSplit.dtsx");
        var plan = PackagePlanner.Plan(package);
        return Assert.Single(plan.Flows).ConditionalSplit
            ?? throw new InvalidOperationException("expected SyntheticConditionalSplit.dtsx's flow to have a ConditionalSplit");
    }

    [Fact]
    public void Emit_ProducesAnIfElseChain_InEvaluationOrder_EndingInTheDefaultBranchIndex()
    {
        var split = LoadConditionalSplitPlan();

        var result = RouterEmitter.Emit("Generated.Mapping", "SplitRouter", "Generated.Sql", "SqlRow", split);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Mapping/SplitRouter.cs", file.RelativePath);

        Assert.Contains("public sealed class SplitRouter : IRowRouter<SqlRow>", file.Content);
        Assert.Contains("public int SelectBranch(SqlRow row, in RowContext ctx)", file.Content);
        Assert.Contains("if ((row.Amount > 1000)) return 0;", file.Content);
        Assert.Contains("return 1; // LowValue", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_EndsInASingleTrailingNewline_NoCarriageReturnsAnywhere()
    {
        var split = LoadConditionalSplitPlan();

        var file = Assert.Single(RouterEmitter.Emit("Generated.Mapping", "SplitRouter", "Generated.Sql", "SqlRow", split).Files);

        Assert.DoesNotContain('\r', file.Content);
        Assert.EndsWith("\n", file.Content);
        Assert.False(file.Content.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Emit_ReportsAGap_WhenACaseExpressionCannotBeTranslated()
    {
        var real = LoadConditionalSplitPlan();

        // Swap in an expression shape ExpressionTranslator.TranslateCondition doesn't support
        // (a bare function call, not a comparison) to prove a translation failure degrades to a
        // gap rather than emitting something wrong.
        var untranslatable = new ConditionalSplitPlan(real.Component,
        [
            new ConditionalSplitBranchPlan(real.Branches[0].OutputName, "UPPER(Amount)", real.Branches[0].DerivedColumns, real.Branches[0].Destination),
            real.Branches[1],
        ]);

        var result = RouterEmitter.Emit("Generated.Mapping", "SplitRouter", "Generated.Sql", "SqlRow", untranslatable);

        Assert.Empty(result.Files);
        var gap = Assert.Single(result.Gaps);
        Assert.Contains("HighValue", gap.Reason);
    }
}
