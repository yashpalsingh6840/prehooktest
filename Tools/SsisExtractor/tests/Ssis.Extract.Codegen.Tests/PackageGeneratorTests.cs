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
        Assert.Contains("Steps: [syntheticPostFlowTargetFlow, sQL_PostLoadStep],", programFile.Content);
        Assert.Contains("PreLoadFileActions: []);", programFile.Content);
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
    public void Generate_TranslatesADataConversion_IntoSsisFnCallsAgainstTheRawSourceColumn()
    {
        // SyntheticDataConversion.dtsx: OLE DB Source (SqlCommand: ID, CustomerIdText,
        // SignupDateText, all strings) -> Data Conversion (DCONV_Types: CustomerIdText ->
        // CustomerId_i4 DT_I4, SignupDateText -> SignupDate_dt DT_DBDATE, both dispositions
        // IgnoreFailure) -> OLE DB Destination. No Derived Column anywhere -- proves the
        // "no Derived Column or Data Conversion found" gate correctly widened rather than
        // blocking this flow. Confirmed beyond this test by actually running the generated
        // exe against .\SQLFORPOC_2022 (seeded with a valid row, a whitespace-padded valid
        // row, an invalid row, an empty-string row, and a NULL row) and reading
        // dbo.SyntheticDataConversionTarget back afterward, matching a real dtexec run of this
        // same fixture row for row -- see Tools/SsisExtractor/CLAUDE.md's own "Data Conversion
        // component" section.
        var package = LoadSyntheticFixture("SyntheticDataConversion.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var entityFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticDataConversionTarget.cs");
        Assert.Contains("public int? CustomerId_i4 { get; set; }", entityFile.Content);
        Assert.Contains("public DateOnly? SignupDate_dt { get; set; }", entityFile.Content);

        var rowFile = Assert.Single(result.Files, f => f.RelativePath == "Sql/SyntheticDataConversionTargetSqlRow.cs");
        Assert.Contains("public string? CustomerIdText { get; set; }", rowFile.Content);
        Assert.Contains("public string? SignupDateText { get; set; }", rowFile.Content);

        var readerFile = Assert.Single(result.Files, f => f.RelativePath == "Sql/SyntheticDataConversionTargetSqlRowReader.cs");
        Assert.Contains("reader.IsDBNull(reader.GetOrdinal(\"CustomerIdText\")) ? null : reader.GetFieldValue<string>(reader.GetOrdinal(\"CustomerIdText\"))", readerFile.Content);

        var transformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticDataConversionTargetTransform.cs");
        Assert.Contains("CustomerId_i4 = SsisFn.ToNullableI4(row.CustomerIdText),", transformFile.Content);
        Assert.Contains("SignupDate_dt = SsisFn.ToNullableDate(row.SignupDateText),", transformFile.Content);

        var ssisFnFile = Assert.Single(result.Files, f => f.RelativePath == "Ssis/SsisFn.cs");
        Assert.Contains("public static int? ToNullableI4(string? value) =>", ssisFnFile.Content);
        Assert.Contains("public static DateOnly? ToNullableDate(string? value) =>", ssisFnFile.Content);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(entityFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(rowFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(readerFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(transformFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(ssisFnFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption, and the unavoidable
        // Notification one -- same shape as every other SqlCommand-sourced flow's own test.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticDataConversion.Notification");
    }

    [Fact]
    public void Generate_ResolvesADataConversionColumn_ReferencedByAConditionalSplitConditionAndAPostSplitDerivedColumn()
    {
        // SyntheticDataConversionSplit.dtsx: OLE DB Source -> Data Conversion (CustomerIdText ->
        // CustomerId_i4, SignupDateText -> SignupDate_dt) -> Conditional Split
        // (!ISNULL(CustomerId_i4) -> Valid; default -> Invalid) -> each branch's own Derived
        // Column (TenureDays <- ISNULL(SignupDate_dt) ? -1 : DATEDIFF(...)) -> Union All -> one
        // destination. Proves the two Data-Conversion cross-reference paths
        // SyntheticDataConversion.dtsx alone never exercises (its converted columns flow
        // straight to a destination, referenced by nothing else) -- the exact shape RBC_Demo_ETL's
        // own Package_Transforms.dtsx (DFT_DerivedAndSplit) needs. Confirmed beyond this test by
        // actually running the generated exe against .\SQLFORPOC_2022 (seeded with a valid row,
        // a valid-CustomerID/NULL-date row, and an invalid-CustomerID row) and reading
        // dbo.SyntheticDataConversionSplitTarget back afterward -- see CLAUDE.md's own "Data
        // Conversion component" section.
        var package = LoadSyntheticFixture("SyntheticDataConversionSplit.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var routerFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/CSPLIT_ValidityRouter.cs");
        Assert.Contains("!((SsisFn.ToNullableI4(row.CustomerIdText) is null))", routerFile.Content);

        var validTransformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticDataConversionSplitTargetValidTransform.cs");
        Assert.Contains("SignupDate_dt = SsisFn.ToNullableDate(row.SignupDateText),", validTransformFile.Content);
        // The null-forgiving "!" before .Value in the non-null ternary branch -- without it,
        // calling SsisFn.ToNullableDate twice (once for the ISNULL check, once here) is a build
        // ERROR (CS8629) under this project's Nullable+TreatWarningsAsErrors, since Roslyn's
        // flow analysis never narrows a repeated METHOD CALL the way it narrows a plain
        // row-property reference. Caught only by actually building the generated project, not
        // unit tests alone.
        Assert.Contains("TenureDays = ((SsisFn.ToNullableDate(row.SignupDateText) is null) ? -(1) : SsisFn.DateDiffDays(SsisFn.ToNullableDate(row.SignupDateText)!.Value, ctx.LoadedAtUtc)),", validTransformFile.Content);

        var invalidTransformFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticDataConversionSplitTargetInvalidTransform.cs");
        Assert.Contains("SignupDate_dt = SsisFn.ToNullableDate(row.SignupDateText),", invalidTransformFile.Content);

        var entityFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticDataConversionSplitTarget.cs");
        Assert.Contains("public int? CustomerId_i4 { get; set; }", entityFile.Content);
        Assert.Contains("public DateOnly? SignupDate_dt { get; set; }", entityFile.Content);

        // The raw source columns feeding the conversion are just as capable of being NULL --
        // both need the same IsDBNull-guarded reader, a real bug caught only by actually running
        // the generated code against a seeded NULL SignupDateText row (ID=2 in the backing
        // table), not by this test alone (see ResolveNullableColumnNames's own doc comment for
        // why it must run before ResolveFlowSource in this Conditional Split code path).
        var readerFile = Assert.Single(result.Files, f => f.RelativePath == "Sql/DFT_DataConversionSplitDemoSqlRowReader.cs");
        Assert.Contains("CustomerIdText = reader.IsDBNull(reader.GetOrdinal(\"CustomerIdText\")) ? null : reader.GetFieldValue<string>(reader.GetOrdinal(\"CustomerIdText\")),", readerFile.Content);
        Assert.Contains("SignupDateText = reader.IsDBNull(reader.GetOrdinal(\"SignupDateText\")) ? null : reader.GetFieldValue<string>(reader.GetOrdinal(\"SignupDateText\")),", readerFile.Content);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(routerFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(validTransformFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(invalidTransformFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(entityFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(readerFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption, and the unavoidable
        // Notification one -- same shape as SyntheticDataConversion.dtsx's own test.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticDataConversionSplit.Notification");
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
    public void Generate_WiresBothFileSystemTaskPositions_PreLoadAndPostFlow()
    {
        // SyntheticFileSystemTask.dtsx: SQL_PreLoad -> FST_PreLoadCopy (pre-load) -> DFT_Load ->
        // FST_PostLoadCopy (post-flow). Confirmed beyond this test by actually building and
        // running the generated exe against .\SQLFORPOC_2022 -- see
        // Tools/SsisExtractor/CLAUDE.md's "File System Task" section.
        var package = LoadSyntheticFixture("SyntheticFileSystemTask.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("var fST_PostLoadCopyStep = new FileSystemStep(\"FST_PostLoadCopy\", new FileSystemPreLoadAction(FileSystemOperation.Copy,", programFile.Content);
        Assert.Contains("Steps: [syntheticFileSystemTaskTargetFlow, fST_PostLoadCopyStep],", programFile.Content);
        Assert.Contains("PreLoadFileActions: [new FileSystemPreLoadAction(FileSystemOperation.Copy,", programFile.Content);
        Assert.Contains("archived-preload.txt", programFile.Content);
        Assert.Contains("archived-postflow.txt", programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Only the unavoidable, non-blocking Notification gap -- this fixture is fully
        // generatable.
        var gap = Assert.Single(result.Gaps);
        Assert.Equal("SyntheticFileSystemTask.Notification", gap.Location);
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
        Assert.Contains("Steps: [dFT_ConditionalSplitDemoSplit],", programFile.Content);
        Assert.Contains("PreLoadFileActions: []);", programFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);

        // Two expected gaps: the SqlCommand-mode column-name assumption (same as every
        // AccessMode=2 OLE DB Source, see BuildSqlFlowSource), and the unavoidable Notification
        // one -- both branches and the router all resolved cleanly, no split-specific gap.
        Assert.Equal(2, result.Gaps.Count);
        Assert.Contains(result.Gaps, g => g.Reason.Contains("assumes each result-set column is named/aliased"));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticConditionalSplit.Notification");
    }

    [Fact]
    public void Generate_WiresTwoFlatFileDestinationFlows_AsDirectCopiesWithFlatFileBulkSinks()
    {
        // SyntheticFlatFileDestination.dtsx: DFT_ExportDelimited/DFT_ExportFixedWidth (both OLE
        // DB Source -> Flat File Destination directly, NO Derived Column -- the exact real
        // evidenced shape of RBC_Demo_ETL's own Package_Exports.dtsx) plus DFT_Log (an ordinary
        // OLE DB Source -> Derived Column -> OLE DB Destination flow, giving the package a real
        // SQL table so AddEtlDbContext<T>() has something to back it). Confirmed beyond this
        // test by actually running the generated exe against .\SQLFORPOC_2022 and reading the
        // real output files/table back afterward (see Tools/SsisExtractor/CLAUDE.md) -- including
        // real fixed-width truncation/padding on a deliberately over-length seeded value.
        var package = LoadSyntheticFixture("SyntheticFlatFileDestination.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // No Model/Mapping row-writer files at all for the flat-file flows beyond the plain
        // source row type + a direct-copy transform -- EntityEmitter/TransformEmitter are reused
        // verbatim, same as any SQL destination.
        Assert.Contains(result.Files, f => f.RelativePath == "Model/DFT_ExportDelimited.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Model/DFT_ExportFixedWidth.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/DFT_ExportDelimitedTransform.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "Mapping/DFT_ExportFixedWidthTransform.cs");
        // No SQL table/DbSet for either flat-file destination -- only DFT_Log's own SyntheticFlatFileDestinationLog does.
        var dbContextFile = Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticFlatFileDestinationDbContext.cs");
        Assert.Contains("public DbSet<SyntheticFlatFileDestinationLog>", dbContextFile.Content);
        Assert.DoesNotContain("DFT_ExportDelimited", dbContextFile.Content);
        Assert.DoesNotContain("DFT_ExportFixedWidth", dbContextFile.Content);

        var programFile = Assert.Single(result.Files, f => f.RelativePath == "Program.cs");
        Assert.Contains("using Etl.Core.Data;", programFile.Content);
        // Flat file sinks are constructed directly (not via AddBulkSink<T>(), which would
        // resolve to a SqlBulkSink<T> -- wrong for a file).
        Assert.DoesNotContain("AddBulkSink<DFT_ExportDelimited>", programFile.Content);
        Assert.DoesNotContain("AddBulkSink<DFT_ExportFixedWidth>", programFile.Content);
        Assert.Contains("builder.Services.AddBulkSink<SyntheticFlatFileDestinationLog>();", programFile.Content);
        Assert.Contains("builder.Services.AddScoped<IBulkSink<DFT_ExportDelimited>>(sp =>", programFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"CustomerID\", null, \",\")", programFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"CleanEmail\", null, \"\\r\\n\")", programFile.Content);
        Assert.Contains("\"CustomerID,FullName,CleanEmail\\r\\n\"", programFile.Content); // the header line
        Assert.Contains("builder.Services.AddScoped<IBulkSink<DFT_ExportFixedWidth>>(sp =>", programFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"CustomerID\", 10, \"\")", programFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"FullName\", 15, \"\")", programFile.Content);
        Assert.Contains("new FlatFileColumnFormat(\"RowEnd\", null, \"\\r\\n\")", programFile.Content);
        Assert.Contains("fileOptions[\"DFT_ExportDelimited\"].ResolvedPath", programFile.Content);
        Assert.Contains("fileOptions[\"DFT_ExportFixedWidth\"].ResolvedPath", programFile.Content);
        Assert.Contains("Steps: [dFT_ExportDelimitedFlow, dFT_ExportFixedWidthFlow, syntheticFlatFileDestinationLogFlow],", programFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(programFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(dbContextFile.Content);

        // Four expected gaps: the SqlCommand-mode column-name assumption on all three OLE DB
        // Sources, plus the unavoidable Notification one -- neither flat-file flow reports its
        // own gap.
        Assert.Equal(4, result.Gaps.Count);
        Assert.Equal(3, result.Gaps.Count(g => g.Reason.Contains("assumes each result-set column is named/aliased")));
        Assert.Contains(result.Gaps, g => g.Location == "SyntheticFlatFileDestination.Notification");
    }

    [Fact]
    public void Generate_WiresAConditionalSplitFlowWithNoPerBranchTransform_ThroughSortAndMerge()
    {
        // SyntheticSortMergeRemerge.dtsx: Conditional Split -> Sort -> Sort -> Merge -> one
        // shared destination, with only a flow-level shared Derived Column (LoadedAtUtc) and NO
        // per-branch transform at all -- the real evidenced shape of RBC_Demo_ETL's own
        // DFT_MergeSortedBranches. Before this round, PackagePlanner.ResolveBranch had no
        // pass-through case for Sort/Merge at all (a fatal "not supported" gap). Confirmed
        // beyond this test by actually building and running the generated exe against
        // .\SQLFORPOC_2022 and reading dbo.SyntheticSortMergeTarget back afterward -- all 4
        // seeded rows landed with the correct Name regardless of Sort's own reordering -- see
        // Tools/SsisExtractor/CLAUDE.md's "Sort + Merge -- Conditional Split remerge" section.
        var package = LoadSyntheticFixture("SyntheticSortMergeRemerge.dtsx");

        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        // ONE entity/table -- both branches converge on the same destination.
        Assert.Single(result.Files, f => f.RelativePath == "Model/SyntheticSortMergeTarget.cs");

        var canadaFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticSortMergeTargetCanadaTransform.cs");
        var restFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/SyntheticSortMergeTargetRestOfWorldTransform.cs");

        // Every destination column is a plain passthrough (no Derived Column of its own beyond
        // the shared LoadedAtUtc) -- proving TransformEmitter's existing "row.PipelineColumnName"
        // fallback resolves correctly straight through Sort/Merge, with zero new
        // value-resolution code needed for this feature.
        Assert.Contains("ID = row.ID,", canadaFile.Content);
        Assert.Contains("Name = row.Name,", canadaFile.Content);
        Assert.Contains("LoadedAtUtc = ctx.LoadedAtUtc,", canadaFile.Content);
        Assert.Contains("ID = row.ID,", restFile.Content);
        Assert.Contains("Name = row.Name,", restFile.Content);
        Assert.Contains("LoadedAtUtc = ctx.LoadedAtUtc,", restFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(canadaFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(restFile.Content);

        var routerFile = Assert.Single(result.Files, f => f.RelativePath == "Mapping/CSPLIT_ByCategoryRouter.cs");
        Assert.Contains("row.Category", routerFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(routerFile.Content);

        // Only the two unavoidable/non-blocking gaps -- the SqlCommand column-name assumption
        // and Notification.
        Assert.Equal(2, result.Gaps.Count);
        Assert.All(result.Gaps, g => Assert.False(g.IsBlocking));
    }
}
