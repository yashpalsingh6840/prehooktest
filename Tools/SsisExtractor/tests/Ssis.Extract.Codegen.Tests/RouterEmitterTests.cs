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

    private static ConditionalSplitPlan LoadFindStringTrimConditionalSplitPlan()
    {
        var package = LoadSyntheticFixture("SyntheticFindStringTrim.dtsx");
        var plan = PackagePlanner.Plan(package);
        return Assert.Single(plan.Flows).ConditionalSplit
            ?? throw new InvalidOperationException("expected SyntheticFindStringTrim.dtsx's flow to have a ConditionalSplit");
    }

    private static DataFlowPlan LoadDataConversionSplitFlow()
    {
        var package = LoadSyntheticFixture("SyntheticDataConversionSplit.dtsx");
        var plan = PackagePlanner.Plan(package);
        return Assert.Single(plan.Flows);
    }

    [Fact]
    public void Emit_TranslatesFindStringTrimCondition_AndReportsBothSsisFunctionsUsed()
    {
        // The real-world motivating case: RBC_Demo_ETL's Package_Transforms.dtsx,
        // CSPLIT_Validity's own condition nests TRIM inside FINDSTRING inside a comparison --
        // proves RouterEmitter (not just ExpressionTranslator in isolation) correctly detects
        // BOTH SsisFn.* calls in the emitted condition text and reports them for SsisFnEmitter,
        // closing the gap where a condition-only usage of a function was previously invisible
        // to the "which SsisFn helpers does this package need" tracking (RouterEmitter never
        // used to return functions used at all -- TransformEmitter's own were the only source).
        var split = LoadFindStringTrimConditionalSplitPlan();

        var result = RouterEmitter.Emit("Generated.Mapping", "SplitRouter", "Generated.Sql", "SqlRow", "Generated.Ssis", split);

        Assert.Empty(result.Result.Gaps);
        var file = Assert.Single(result.Result.Files);
        Assert.Contains("using Generated.Ssis;", file.Content);
        Assert.Contains("SsisFn.FindString(SsisFn.Trim(row.Email), \"@\", 1) > 0", file.Content);
        Assert.Equal(new HashSet<string> { "FindString", "Trim" }, result.SsisFunctionsUsed);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ResolvesAConditionReferencingADataConversionColumn_ViaSsisFnToNullable()
    {
        // The real-world motivating case: RBC_Demo_ETL's Package_Transforms.dtsx,
        // CSPLIT_Validity's own condition references CustomerID_i4 -- a Data Conversion output
        // column, not a plain buffer column -- via !ISNULL(...). Without dataConversion/pipeline
        // passed through, the pre-existing reference-building here would have resolved it as an
        // ordinary "row.CustomerId_i4" (which doesn't exist on the row type at all, since the
        // conversion is computed inline, never materialized as a row property) -- proves the
        // cross-reference resolves to the actual SsisFn.ToNullableI4 call against the RAW source
        // column instead.
        var flow = LoadDataConversionSplitFlow();
        var split = flow.ConditionalSplit ?? throw new InvalidOperationException("expected SyntheticDataConversionSplit.dtsx's flow to have a ConditionalSplit");

        var result = RouterEmitter.Emit("Generated.Mapping", "SplitRouter", "Generated.Sql", "SqlRow", "Generated.Ssis", split, flow.Pipeline, flow.DataConversion);

        Assert.Empty(result.Result.Gaps);
        var file = Assert.Single(result.Result.Files);
        Assert.Contains("!((SsisFn.ToNullableI4(row.CustomerIdText) is null))", file.Content);
        Assert.Equal(new HashSet<string> { "ToNullableI4" }, result.SsisFunctionsUsed);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ProducesAnIfElseChain_InEvaluationOrder_EndingInTheDefaultBranchIndex()
    {
        var split = LoadConditionalSplitPlan();

        var result = RouterEmitter.Emit("Generated.Mapping", "SplitRouter", "Generated.Sql", "SqlRow", "Generated.Ssis", split);

        Assert.Empty(result.Result.Gaps);
        var file = Assert.Single(result.Result.Files);
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

        var file = Assert.Single(RouterEmitter.Emit("Generated.Mapping", "SplitRouter", "Generated.Sql", "SqlRow", "Generated.Ssis", split).Result.Files);

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

        var result = RouterEmitter.Emit("Generated.Mapping", "SplitRouter", "Generated.Sql", "SqlRow", "Generated.Ssis", untranslatable);

        Assert.Empty(result.Result.Files);
        var gap = Assert.Single(result.Result.Gaps);
        Assert.Contains("HighValue", gap.Reason);
    }
}
