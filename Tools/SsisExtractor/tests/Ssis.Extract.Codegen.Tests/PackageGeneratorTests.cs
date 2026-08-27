using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

public class PackageGeneratorTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    // tests/Ssis.Extract.Codegen.Tests -> tests/Ssis.Extract.Tests/Fixtures.
    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Generate_WiresAPostFlowExecuteSqlTask_IntoProgramCsAfterTheFlowItFollows()
    {
        // SyntheticPostFlowSql.dtsx: SQL_PreLoad -> DFT_Load (Flat File Source -> Derived
        // Column -> OLE DB Destination) -> SQL_PostLoad. Unlike SyntheticParallelShapes.dtsx's
        // own post-flow Execute SQL Task (which follows a Data Flow Task with no Derived
        // Column and so never gets wired), this fixture's flow IS wired, so it's the one that
        // proves the post-flow SQL step reaches a real, compiling Program.cs -- confirmed
        // beyond this test by actually running the generated exe against .\SQLFORPOC_2022 and
        // reading dbo.SyntheticPostFlowTarget back afterward (see Tools/SsisExtractor/CLAUDE.md).
        var package = LoadSyntheticFixture("SyntheticPostFlowSql.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("var syntheticPostFlowTargetFlow = new DataFlowStep<", programFile.Content);
        Assert.Contains(
            "var sQL_PostLoadStep = new ExecuteSqlStep(\"SQL_PostLoad\", \"UPDATE dbo.SyntheticPostFlowTarget SET Name = UPPER(Name);\", sp.GetRequiredService<ILogger<ExecuteSqlStep>>());",
            programFile.Content);
        Assert.Contains("Steps: [syntheticPostFlowTargetFlow, sQL_PostLoadStep]);", programFile.Content);
        Assert.Contains("PreLoadStatements: [\"TRUNCATE TABLE dbo.SyntheticPostFlowTarget;\"],", programFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // The only real gap is the unavoidable, non-blocking Notification one.
        var gap = Assert.Single(result.Gaps);
        Assert.Equal("SyntheticPostFlowSql.Notification", gap.Location);
    }

    [Fact]
    public void Generate_WiresAnOleDbSourcedFlow_IntoProgramCsViaSqlRowSource()
    {
        // SyntheticOleDbSourceTransform.dtsx: OLE DB Source (SqlCommand, AccessMode=2) ->
        // Derived Column -> OLE DB Destination. Confirmed beyond this test by actually
        // running the generated exe against .\SQLFORPOC_2022 and reading
        // dbo.SyntheticOleDbSourceTarget back afterward (see Tools/SsisExtractor/CLAUDE.md).
        var package = LoadSyntheticFixture("SyntheticOleDbSourceTransform.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.Contains(result.Files, f => f.RelativePath == "Sql/SyntheticOleDbSourceTargetSqlRow.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Sql/SyntheticOleDbSourceTargetSqlRowReader.cs");

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("using Etl.Core.Data;", programFile.Content);
        Assert.Contains("var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;", programFile.Content);
        Assert.Contains(
            "CommandText = \"SELECT ID, Amount FROM dbo.SyntheticOleDbSourceInput\" };",
            programFile.Content);
        Assert.Contains("return new SqlRowSource<SyntheticOleDbSourceTargetSqlRow>(\"OLE DB Source\", sqlOptions, SyntheticOleDbSourceTargetSqlRowReader.Read);", programFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption, and the unavoidable
        // Notification one.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticOleDbSourceTransform.Notification");
    }

    [Fact]
    public void Generate_ProducesAReferenceTableCacheScaffold_ForALookup_ButDoesNotWireItIntoProgramCs()
    {
        // SyntheticLookupSplit.dtsx: OLE DB Source -> Lookup -> Conditional Split -> 3 OLE DB
        // Destinations. Conditional Split's mere presence causes no separate gap (nothing
        // inspects ConditionalSplitPayload yet) -- this test is purely about the Lookup gate.
        var package = LoadSyntheticFixture("SyntheticLookupSplit.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var cacheFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/LookupCache.cs");
        Assert.Contains("public static class LookupCache", cacheFile.Content);
        Assert.Contains("public sealed class ReferenceRow", cacheFile.Content);
        Assert.Contains("public int CustomerID { get; set; }", cacheFile.Content);
        Assert.Contains("command.CommandText = \"SELECT CustomerID, CustomerName, Region FROM dbo.SyntheticCustomer\";", cacheFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(cacheFile.Content);

        // No Program.cs at all -- the Lookup flow is this fixture's only Data Flow Task, and
        // it was correctly never wired.
        Assert.DoesNotContain(result.Files, f => f.RelativePath == "Program.cs");

        Assert.Contains(result.Gaps, g => g.Reason.Contains("SSIS does not persist a Lookup's join key")
            && g.Reason.Contains("Mapping/LookupCache.cs"));
    }

    [Fact]
    public void Generate_MergesTwoBranchesIntoOneSharedEntityAndTable_WhenAUnionAllRemergesThem()
    {
        // SyntheticConditionalSplitRemerge.dtsx: two branches, each through their own "tag"
        // Derived Column, remerge via a Union All into ONE shared OLE DB Destination -- the
        // real-world shape RBC_Demo_ETL's Package_Transforms.dtsx (DFT_DerivedAndSplit) has.
        // Confirmed beyond this test by actually building and running the generated exe
        // against .\SQLFORPOC_2022 and reading dbo.SyntheticRemergeTarget back afterward,
        // including the exact "Amount > 1000" boundary row (ID=3, Amount=1000.00 -> Low, not
        // High) -- see Tools/SsisExtractor/CLAUDE.md's "Conditional Split -- multi-hop Union
        // All remerge" section.
        var package = LoadSyntheticFixture("SyntheticConditionalSplitRemerge.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // ONE entity/table, not two -- both branches converge on the same destination.
        Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticRemergeTarget.cs");

        // Two DISTINCT transforms, disambiguated by branch output name since they'd otherwise
        // both be "SyntheticRemergeTargetTransform".
        var highFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticRemergeTargetHighTransform.cs");
        var lowFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticRemergeTargetLowTransform.cs");

        // Each transform combines the flow's shared Derived Column (LoadedAtUtc) with its OWN
        // per-branch one (Segment) -- proving TransformEmitter's merged-DerivedColumns list
        // resolves columns from both, not just one.
        Assert.Contains("LoadedAtUtc = ctx.LoadedAtUtc,", highFile.Content);
        Assert.Contains("Segment = \"High\",", highFile.Content);
        Assert.Contains("LoadedAtUtc = ctx.LoadedAtUtc,", lowFile.Content);
        Assert.Contains("Segment = \"Low\",", lowFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(highFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(lowFile.Content);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        // ONE AddBulkSink<> call, not two -- PortfolioDigest/ProgramEmitter already dedupe by
        // entity name (unaffected by this change), reconfirmed here for the converged case.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(programFile.Content, "AddBulkSink<SyntheticRemergeTarget>"));
        // No per-branch IRowTransform<> DI registration -- see ProgramEmitter's own comment on
        // why (two branches sharing an entity would collide on the same closed generic).
        Assert.DoesNotContain("AddScoped<IRowTransform<", programFile.Content);
        Assert.Contains("new SyntheticRemergeTargetHighTransform(),", programFile.Content);
        Assert.Contains("new SyntheticRemergeTargetLowTransform(),", programFile.Content);
        // Both branches still share the one bulk sink for the one shared table.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(programFile.Content, "GetRequiredService<IBulkSink<SyntheticRemergeTarget>>\\(\\)").Count);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Only the two unavoidable/non-blocking gaps -- the SqlCommand column-name assumption
        // and Notification. Confirms PortfolioDigest.IsBlockingGap would count this package as
        // fully generatable (0 blocking gaps), not just "produces some files".
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
    }

    [Fact]
    public void Generate_WiresAConditionalSplit_IntoProgramCsWithOneBranchPerDestination()
    {
        // SyntheticConditionalSplit.dtsx: OLE DB Source -> Derived Column (LoadedAtUtc) ->
        // Conditional Split (one case, "Amount > 1000" -> HighValue; default -> LowValue) -> 2
        // OLE DB Destinations. Confirmed beyond this test by actually running the generated exe
        // against .\SQLFORPOC_2022 and reading dbo.SyntheticHighValue/SyntheticLowValue back
        // afterward (see Tools/SsisExtractor/CLAUDE.md).
        var package = LoadSyntheticFixture("SyntheticConditionalSplit.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.Contains(result.Files, f => f.RelativePath == "Model/SyntheticHighValue.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Model/SyntheticLowValue.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/SyntheticHighValueTransform.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/SyntheticLowValueTransform.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/ConditionalSplitRouter.cs");

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("using Etl.Core.Data;", programFile.Content);
        Assert.Contains("builder.Services.AddBulkSink<SyntheticHighValue>();", programFile.Content);
        Assert.Contains("builder.Services.AddBulkSink<SyntheticLowValue>();", programFile.Content);
        Assert.Contains("builder.Services.AddScoped<IRowRouter<DFT_ConditionalSplitDemoSqlRow>, ConditionalSplitRouter>();", programFile.Content);
        // No per-branch IRowTransform<> DI registration -- each branch's transform is
        // constructed directly (`new`) below, since two branches can share EntityName (a Union
        // All remerge) and would otherwise collide on the same closed generic registration.
        Assert.DoesNotContain("AddScoped<IRowTransform<DFT_ConditionalSplitDemoSqlRow, SyntheticHighValue>", programFile.Content);
        Assert.DoesNotContain("AddScoped<IRowTransform<DFT_ConditionalSplitDemoSqlRow, SyntheticLowValue>", programFile.Content);
        Assert.Contains("var dFT_ConditionalSplitDemoSplit = new ConditionalSplitStep<DFT_ConditionalSplitDemoSqlRow>(", programFile.Content);
        Assert.Contains("new ConditionalSplitBranch<DFT_ConditionalSplitDemoSqlRow, SyntheticHighValue>(", programFile.Content);
        Assert.Contains("new ConditionalSplitBranch<DFT_ConditionalSplitDemoSqlRow, SyntheticLowValue>(", programFile.Content);
        Assert.Contains("new SyntheticHighValueTransform(),", programFile.Content);
        Assert.Contains("new SyntheticLowValueTransform(),", programFile.Content);
        Assert.Contains("Steps: [dFT_ConditionalSplitDemoSplit]);", programFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption (same as every
        // AccessMode=2 OLE DB Source, see BuildSqlFlowSource), and the unavoidable Notification
        // one -- both branches and the router all resolved cleanly, no split-specific gap.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticConditionalSplit.Notification");
    }
}
