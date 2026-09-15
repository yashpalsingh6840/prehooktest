using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Direct unit coverage for <see cref="ComponentTestEmitter.EmitSequenceContainerTest"/>,
/// <see cref="ComponentTestEmitter.EmitOleDbCommandTest"/>,
/// <see cref="ComponentTestEmitter.EmitMergeJoinMapperTest"/>,
/// <see cref="ComponentTestEmitter.EmitForEachLoopTest"/>,
/// <see cref="ComponentTestEmitter.EmitMulticastTest"/>,
/// <see cref="ComponentTestEmitter.EmitAggregateSourceTest"/>,
/// <see cref="ComponentTestEmitter.EmitSqlSourceTest"/>, and
/// <see cref="ComponentTestEmitter.EmitExcelSourceTest"/> -- deliberately NOT real-fixture/
/// dtexec-verified end-to-end tests the way most other emitters in this project are proven,
/// because none of these measures a new SSIS RUNTIME semantic (unlike e.g. a numeric-coercion
/// round): a Sequence Container running its children sequentially, in declared order, is already
/// established by <c>PackagePlanner</c>'s own topological ordering; <c>OleDbCommandStep&lt;TRow&gt;</c>
/// running its own SQL template once per source row, a Merge Join mapper's own <c>LeftKey</c>/
/// <c>RightKey</c>/<c>Map</c>, <c>ForEachLoopStep</c>'s own per-NameMode <c>currentFile</c>
/// computation, <c>MulticastStep</c>'s own "every row to every branch" fan-out, and
/// <c>AggregateRowSource</c>'s own grouping/NULL-exclusion, are each already established by that
/// generated code's own implementation, unchanged by this tool.
/// <c>FakeUnitOfWork.ExecutedSql</c>/<c>ExecutedParameterizedSql</c>/<c>BulkInserts</c> are plain
/// <c>List&lt;T&gt;.Add</c> calls with no concurrency involved. What these tests actually prove is
/// mechanical: that the emitted TEXT contains the right assertions for a given input -- the same
/// "mechanical parity, not new semantics" tier this project already uses elsewhere (e.g.
/// <c>EntityEmitter</c>'s own string-nullability fix). Every PRIMARY, real-evidenced shape
/// (Sequence Container: one SQL child + one File System Task child, matching
/// <c>Package_Advanced.dtsx</c>'s own <c>SEQ_Prepare</c>; OLE DB Command: one parameter column,
/// matching that same package's own <c>DFT_FlagCustomers</c>; Merge Join mapper: two
/// Data-Conversion-derived join keys plus a mixed left/right column set, matching
/// <c>Package_Transforms.dtsx</c>'s own <c>DFT_SortAndMergeJoin</c>; ForEach loop: NameAndExtension
/// mode, matching that same package's own <c>FEL_SampleFiles</c>; Multicast: one SQL branch plus
/// one Flat File Destination branch, matching <c>Package_Legacy.dtsx</c>'s own
/// <c>DFT_FixedWidthImport</c>; Aggregate source: one string GroupBy key plus one Count function,
/// matching the dedicated <c>SyntheticAggregate.dtsx</c> fixture -- the real evidenced
/// <c>AGG_ByRegion</c> is Lookup-gated and unreachable, see <c>GenerateAggregateFlow</c>'s own doc
/// comment) is independently verified end-to-end against the real portfolio -- generated, built,
/// and run -- not just here.
/// </summary>
public class ComponentTestEmitterTests
{
    [Fact]
    public void EmitSequenceContainerTest_AssertsRelativeOrder_ForConsecutiveSqlChildren()
    {
        var children = new List<ProgramStep>
        {
            new ProgramSqlStep("SQL_First", "SQL_FirstStatement"),
            new ProgramSqlStep("SQL_Second", "SQL_SecondStatement"),
            new ProgramSqlStep("SQL_Third", "SQL_ThirdStatement"),
        };

        var file = ComponentTestEmitter.EmitSequenceContainerTest("MyPackage.Tests", "MyPackage", "SEQ_Batch", children);

        Assert.NotNull(file);
        Assert.Contains("Assert.Contains(SQL_FirstStatement.BuildStatement(), uow.ExecutedSql);", file!.Content);
        Assert.Contains("Assert.Contains(SQL_SecondStatement.BuildStatement(), uow.ExecutedSql);", file.Content);
        Assert.Contains("Assert.Contains(SQL_ThirdStatement.BuildStatement(), uow.ExecutedSql);", file.Content);
        // Order is asserted only between CONSECUTIVE children -- two comparisons for three
        // children, not every pair.
        Assert.Contains("Assert.True(uow.ExecutedSql.IndexOf(SQL_FirstStatement.BuildStatement()) < uow.ExecutedSql.IndexOf(SQL_SecondStatement.BuildStatement()));", file.Content);
        Assert.Contains("Assert.True(uow.ExecutedSql.IndexOf(SQL_SecondStatement.BuildStatement()) < uow.ExecutedSql.IndexOf(SQL_ThirdStatement.BuildStatement()));", file.Content);
        Assert.DoesNotContain("IndexOf(SQL_FirstStatement.BuildStatement()) < uow.ExecutedSql.IndexOf(SQL_ThirdStatement", file.Content);
    }

    [Fact]
    public void EmitSequenceContainerTest_MixesSqlAndFileSystemChildren_WithNoCrossKindOrderAssertion()
    {
        var children = new List<ProgramStep>
        {
            new ProgramSqlStep("SQL_TruncateTargets", "SQL_TruncateTargetsStatement"),
            new ProgramFileSystemStep("FST_ArchiveWorkbook",
                new FileSystemActionPlan("Copy", "unused-literal-source", "unused-literal-dest", true, "CM_FILE_Source", "CM_FILE_Dest")),
        };

        var file = ComponentTestEmitter.EmitSequenceContainerTest("MyPackage.Tests", "MyPackage", "SEQ_Prepare", children);

        Assert.NotNull(file);
        Assert.Contains("await File.WriteAllTextAsync(harness.FileSystemTaskPath(\"CM_FILE_Source\")", file!.Content);
        Assert.Contains("Assert.Contains(SQL_TruncateTargetsStatement.BuildStatement(), uow.ExecutedSql);", file.Content);
        Assert.Contains("Assert.True(File.Exists(harness.FileSystemTaskPath(\"CM_FILE_Dest\")));", file.Content);
        // Only one SQL child -- nothing to compare it against, so no IndexOf assertion at all.
        Assert.DoesNotContain("IndexOf", file.Content);
    }

    [Fact]
    public void EmitSequenceContainerTest_ReturnsNull_WhenAnyChildIsAnUnsupportedKind()
    {
        var children = new List<ProgramStep>
        {
            new ProgramSqlStep("SQL_First", "SQL_FirstStatement"),
            new ProgramScriptTaskStep("SCR_DoSomething", "SCR_DoSomethingTask"),
        };

        var file = ComponentTestEmitter.EmitSequenceContainerTest("MyPackage.Tests", "MyPackage", "SEQ_Mixed", children);

        Assert.Null(file);
    }

    [Fact]
    public void EmitSequenceContainerTest_ReturnsNull_ForAFileSystemChildOutsideThePilotsOwnScope()
    {
        var children = new List<ProgramStep>
        {
            // Move, not Copy -- unsupported, same as EmitFileSystemTaskTest's own scope.
            new ProgramFileSystemStep("FST_Move",
                new FileSystemActionPlan("Move", "unused-literal-source", "unused-literal-dest", true, "CM_FILE_Source", "CM_FILE_Dest")),
        };

        var file = ComponentTestEmitter.EmitSequenceContainerTest("MyPackage.Tests", "MyPackage", "SEQ_Move", children);

        Assert.Null(file);
    }

    [Fact]
    public void EmitSequenceContainerTest_ReturnsNull_ForNoChildren()
    {
        var file = ComponentTestEmitter.EmitSequenceContainerTest("MyPackage.Tests", "MyPackage", "SEQ_Empty", []);

        Assert.Null(file);
    }

    private static ResolvedColumn Column(string name, string? pipelineDataType) =>
        new(name, name, pipelineDataType, null, null, null, null,
            pipelineDataType is null ? null : SsisPipelineTypeMap.Resolve(pipelineDataType), $"{name}Ref");

    [Fact]
    public void EmitOleDbCommandTest_AssertsTheSqlTemplateAndPositionalParameters_ForEveryRow()
    {
        // Mirrors DFT_FlagCustomers' own real shape -- one parameter column, plus a second,
        // non-parameter column that must still be synthesized on the row (it is a real property
        // of the row type, whether or not the command itself references it).
        var columns = new List<ResolvedColumn> { Column("CustomerID", "i4"), Column("Notes", "wstr") };

        var file = ComponentTestEmitter.EmitOleDbCommandTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Sql", "MyRow",
            "OLECMD_SetFlag", "EXEC dbo.usp_SetFlag {0}", ["CustomerID"], columns);

        Assert.NotNull(file);
        Assert.Contains("new MyRow { CustomerID = 7, Notes = \"Sample\" },", file!.Content);
        Assert.Contains("row => new object?[] { row.CustomerID }", file.Content);
        Assert.Contains("Assert.Equal(2, uow.ExecutedParameterizedSql.Count);", file.Content);
        Assert.Contains("Assert.Equal(\"EXEC dbo.usp_SetFlag {0}\", call.Sql);", file.Content);
        Assert.Contains("Assert.Equal(new object?[] { 7 }, call.Parameters);", file.Content);
    }

    [Fact]
    public void EmitOleDbCommandTest_ReturnsNull_WhenAParameterColumnHasNoResolvableClrType()
    {
        // The parameter column's own external data type is not one SsisPipelineTypeMap
        // recognizes at all -- Type resolves to null, so there is nothing safe to put in the
        // synthesized row for it, let alone the parameter array.
        var columns = new List<ResolvedColumn> { Column("SomeExoticColumn", pipelineDataType: null) };

        var file = ComponentTestEmitter.EmitOleDbCommandTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Sql", "MyRow",
            "OLECMD_X", "EXEC dbo.usp_X {0}", ["SomeExoticColumn"], columns);

        Assert.Null(file);
    }

    [Fact]
    public void EmitOleDbCommandTest_ReturnsNull_ForNoParameterColumns()
    {
        var file = ComponentTestEmitter.EmitOleDbCommandTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Sql", "MyRow",
            "OLECMD_X", "EXEC dbo.usp_NoOp", [], [Column("Unrelated", "i4")]);

        Assert.Null(file);
    }

    [Fact]
    public void EmitMergeJoinMapperTest_AssertsKeysAndColumns_ForAMatchedPairAndAnUnmatchedLeftRow()
    {
        // Mirrors DFT_SortAndMergeJoin's own real shape: both join keys ARE Data-Conversion-derived
        // (SsisFn.ToNullableI4), one left-side column is a plain passthrough, one right-side column
        // is a plain passthrough -- proving both RawAssignment paths (conversion + plain) compose
        // correctly in one test.
        var leftKey = new MergeJoinEmitter.MergeJoinTestKey("CustomerID", "string", "i4", null);
        var rightKey = new MergeJoinEmitter.MergeJoinTestKey("CustomerID", "string", "i4", null);
        var columns = new List<MergeJoinEmitter.MergeJoinTestColumn>
        {
            new("Email", MayBeAbsent: false, "Email", "string", null, null),
            new("ContactPhone", MayBeAbsent: true, "ContactPhone", "string", null, null),
        };

        var file = ComponentTestEmitter.EmitMergeJoinMapperTest(
            "MyPackage.Tests", "MyPackage.Csv", "MyLeftRow", "MyPackage.Csv", "MyRightRow",
            "MyPackage.Mapping", "MyMapper", leftKey, rightKey, columns);

        Assert.NotNull(file);
        Assert.Contains("new MyLeftRow { CustomerID = \"123\", Email = \"Sample\" };", file!.Content);
        Assert.Contains("new MyRightRow { CustomerID = \"123\", ContactPhone = \"Sample\" };", file.Content);
        Assert.Contains("Assert.Equal((int?)123, MyMapper.LeftKey(left));", file.Content);
        Assert.Contains("Assert.Equal((int?)123, MyMapper.RightKey(right));", file.Content);
        Assert.Contains("Assert.Equal(\"Sample\", matched.Email);", file.Content);
        Assert.Contains("Assert.Equal(\"Sample\", matched.ContactPhone);", file.Content);
        // The unmatched-left-row scenario: left-side columns still assert their value; the
        // right-side one asserts null instead.
        Assert.Contains("MyMapper.Map(left, null);", file.Content);
        Assert.Contains("Assert.Equal(\"Sample\", unmatched.Email);", file.Content);
        Assert.Contains("Assert.Null(unmatched.ContactPhone);", file.Content);
    }

    [Fact]
    public void EmitMergeJoinMapperTest_OmitsTheUnmatchedRowScenario_WhenNoColumnMayBeAbsent()
    {
        var leftKey = new MergeJoinEmitter.MergeJoinTestKey("Id", "int", null, null);
        var rightKey = new MergeJoinEmitter.MergeJoinTestKey("Id", "int", null, null);
        var columns = new List<MergeJoinEmitter.MergeJoinTestColumn>
        {
            new("Name", MayBeAbsent: false, "Name", "string", null, null),
        };

        var file = ComponentTestEmitter.EmitMergeJoinMapperTest(
            "MyPackage.Tests", "MyPackage.Sql", "MyLeftRow", "MyPackage.Sql", "MyRightRow",
            "MyPackage.Mapping", "MyMapper", leftKey, rightKey, columns);

        Assert.NotNull(file);
        Assert.DoesNotContain("Map_LeavesRightSideColumnsNull", file!.Content);
    }

    [Fact]
    public void EmitMergeJoinMapperTest_ReturnsNull_WhenAKeyHasAnUnsupportedConversionTarget()
    {
        var leftKey = new MergeJoinEmitter.MergeJoinTestKey("Id", "string", "unsupportedtype", null);
        var rightKey = new MergeJoinEmitter.MergeJoinTestKey("Id", "int", null, null);

        var file = ComponentTestEmitter.EmitMergeJoinMapperTest(
            "MyPackage.Tests", "MyPackage.Sql", "MyLeftRow", "MyPackage.Sql", "MyRightRow",
            "MyPackage.Mapping", "MyMapper", leftKey, rightKey, []);

        Assert.Null(file);
    }

    [Fact]
    public void EmitForEachLoopTest_UsesTheLiteralFileName_ForNameAndExtension()
    {
        var file = ComponentTestEmitter.EmitForEachLoopTest(
            "MyPackage.Tests", "MyPackage", "FEL_Loop", "FEL_Loop", "*.csv", "NameAndExtension", "SQL_LogStatement");

        Assert.NotNull(file);
        Assert.Contains("Assert.Contains(SQL_LogStatement.BuildStatement(\"a.csv\"), uow.ExecutedSql);", file!.Content);
        Assert.Contains("Assert.Contains(SQL_LogStatement.BuildStatement(\"b.csv\"), uow.ExecutedSql);", file.Content);
        Assert.Contains("uow.ExecutedSql.IndexOf(SQL_LogStatement.BuildStatement(\"a.csv\")) < uow.ExecutedSql.IndexOf(SQL_LogStatement.BuildStatement(\"b.csv\"))", file.Content);
    }

    [Fact]
    public void EmitForEachLoopTest_StripsTheExtension_ForNameOnly()
    {
        var file = ComponentTestEmitter.EmitForEachLoopTest(
            "MyPackage.Tests", "MyPackage", "FEL_Loop", "FEL_Loop", "*.csv", "NameOnly", "SQL_LogStatement");

        Assert.NotNull(file);
        Assert.Contains("SQL_LogStatement.BuildStatement(\"a\")", file!.Content);
        Assert.Contains("SQL_LogStatement.BuildStatement(\"b\")", file.Content);
        Assert.DoesNotContain("BuildStatement(\"a.csv\")", file.Content);
    }

    [Fact]
    public void EmitForEachLoopTest_BuildsTheRealTempFolderPath_ForFullyQualified()
    {
        var file = ComponentTestEmitter.EmitForEachLoopTest(
            "MyPackage.Tests", "MyPackage", "FEL_Loop", "FEL_Loop", "*.csv", "FullyQualified", "SQL_LogStatement");

        Assert.NotNull(file);
        Assert.Contains("SQL_LogStatement.BuildStatement(Path.Combine(harness.ForEachLoopFolder(\"FEL_Loop\"), \"a.csv\"))", file!.Content);
        Assert.Contains("SQL_LogStatement.BuildStatement(Path.Combine(harness.ForEachLoopFolder(\"FEL_Loop\"), \"b.csv\"))", file.Content);
    }

    [Theory]
    [InlineData("data??.csv")]
    [InlineData("prefix*.csv")]
    [InlineData("*.csv.*")]
    public void EmitForEachLoopTest_ReturnsNull_ForAFileSpecMoreComplexThanASingleWildcard(string fileSpec)
    {
        var file = ComponentTestEmitter.EmitForEachLoopTest(
            "MyPackage.Tests", "MyPackage", "FEL_Loop", "FEL_Loop", fileSpec, "NameAndExtension", "SQL_LogStatement");

        Assert.Null(file);
    }

    [Fact]
    public void EmitMulticastTest_AssertsBothTheSqlBranchAndTheFlatFileBranch_ForARealMixedShape()
    {
        // Mirrors DFT_FixedWidthImport's own real shape -- one SQL branch, one Flat File
        // Destination branch, sharing one upstream source row.
        var columns = new List<ResolvedColumn> { Column("CustomerID", "wstr"), Column("CountryCode", "wstr") };
        var branches = new List<ComponentTestEmitter.MulticastTestBranch>
        {
            new("Multicast Output 1", "MyPackage.Model", "CustomerFixedImport", "CustomerFixedImportTransform", new SqlFlowSink("OLEDST_CustomerFixedImport")),
            new("Multicast Output 2", "MyPackage.Model", "AuditTrail", "AuditTrailTransform",
                new FlatFileFlowSink("FFDST_AuditTrail", "Audit", true, null, [new FlatFileColumnFormatSpec("CustomerID", null, ","), new FlatFileColumnFormatSpec("CountryCode", null, "\r\n")])),
        };

        var file = ComponentTestEmitter.EmitMulticastTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Csv", "MyRow", "DFT_FixedWidthImport", columns, branches);

        Assert.NotNull(file);
        Assert.Contains("new MyRow { CustomerID = \"Sample\", CountryCode = \"Sample\" },", file!.Content);
        Assert.Contains("new ConditionalSplitBranch<MyRow, CustomerFixedImport>(\"Multicast Output 1\", new CustomerFixedImportTransform(), new SqlBulkSink<CustomerFixedImport>(Options.Create(new BulkCopyOptions()), NullLogger<SqlBulkSink<CustomerFixedImport>>.Instance)),", file.Content);
        Assert.Contains("new FlatFileBulkSink<AuditTrail>(tempFile1, true, null,", file.Content);
        Assert.Contains("Assert.Single(uow.BulkInserts);", file.Content);
        Assert.Contains("Assert.All(uow.BulkInserts, call => Assert.Equal(2, call.RowsWritten));", file.Content);
        Assert.Contains("Assert.Equal(2, (await File.ReadAllLinesAsync(tempFile1)).Length);", file.Content);
        Assert.Contains("if (File.Exists(tempFile1)) File.Delete(tempFile1);", file.Content);
    }

    [Fact]
    public void EmitMulticastTest_UsesAssertEmpty_WhenEveryBranchIsFlatFile()
    {
        var columns = new List<ResolvedColumn> { Column("CustomerID", "wstr") };
        var branches = new List<ComponentTestEmitter.MulticastTestBranch>
        {
            new("Output 1", "MyPackage.Model", "AuditA", "AuditATransform", new FlatFileFlowSink("FFDST_AuditA", "A", true, null, [new FlatFileColumnFormatSpec("CustomerID", null, "\r\n")])),
        };

        var file = ComponentTestEmitter.EmitMulticastTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Csv", "MyRow", "DFT_AllFlatFile", columns, branches);

        Assert.NotNull(file);
        Assert.Contains("Assert.Empty(uow.BulkInserts);", file!.Content);
    }

    [Fact]
    public void EmitMulticastTest_ReturnsNull_WhenThereAreNoBranches()
    {
        var columns = new List<ResolvedColumn> { Column("CustomerID", "wstr") };

        var file = ComponentTestEmitter.EmitMulticastTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Csv", "MyRow", "DFT_Empty", columns, []);

        Assert.Null(file);
    }

    [Fact]
    public void EmitMulticastTest_ReturnsNull_WhenNoSourceColumnHasAResolvableClrType()
    {
        var columns = new List<ResolvedColumn> { Column("SomeExoticColumn", pipelineDataType: null) };
        var branches = new List<ComponentTestEmitter.MulticastTestBranch>
        {
            new("Output 1", "MyPackage.Model", "Entity", "EntityTransform", new SqlFlowSink("OLEDST_Entity")),
        };

        var file = ComponentTestEmitter.EmitMulticastTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Csv", "MyRow", "DFT_NoResolvableColumns", columns, branches);

        Assert.Null(file);
    }

    [Fact]
    public void EmitAggregateSourceTest_AssertsGroupedKeysAndNonNullCounts_ForARealShape()
    {
        // Mirrors AGG_ByRegion's own real shape -- one string GroupBy key, one Count function.
        var functions = new List<ComponentTestEmitter.AggregateSourceTestFunction>
        {
            new("CustomerCount", "CustomerID", "string", AggregationTypeRaw: 1),
        };

        var file = ComponentTestEmitter.EmitAggregateSourceTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Sql", "SourceRow", "MyPackage.Sql", "TargetAggregateRow",
            "AGG_ByRegion", "Region", "string", functions);

        Assert.NotNull(file);
        Assert.Contains("new SourceRow { Region = \"GroupA\", CustomerID = \"Sample\" },", file!.Content);
        Assert.Contains("new SourceRow { Region = \"GroupA\", CustomerID = null },", file.Content);
        Assert.Contains("new SourceRow { Region = \"GroupB\", CustomerID = \"Sample\" },", file.Content);
        Assert.Contains("row => row.Region", file.Content);
        Assert.Contains("CustomerCount = groupedRows.Count(r => r.CustomerID != null)", file.Content);
        Assert.Contains("Assert.Equal(2, groupA.CustomerCount);", file.Content);
        Assert.Contains("Assert.Equal(1, groupB.CustomerCount);", file.Content);
    }

    [Fact]
    public void EmitAggregateSourceTest_ReturnsNull_ForANonStringGroupByKey()
    {
        var functions = new List<ComponentTestEmitter.AggregateSourceTestFunction>
        {
            new("Total", "Amount", "int", AggregationTypeRaw: 1),
        };

        var file = ComponentTestEmitter.EmitAggregateSourceTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Sql", "SourceRow", "MyPackage.Sql", "TargetAggregateRow",
            "AGG_ByYear", "Year", "int", functions);

        Assert.Null(file);
    }

    [Fact]
    public void EmitAggregateSourceTest_ReturnsNull_ForAnUnsupportedAggregationType()
    {
        // AggregationType 4 (Sum) -- PlanAggregate never actually generates this shape today, but
        // guard it anyway rather than emit an assertion for semantics this tool doesn't produce.
        var functions = new List<ComponentTestEmitter.AggregateSourceTestFunction>
        {
            new("TotalAmount", "Amount", "decimal", AggregationTypeRaw: 4),
        };

        var file = ComponentTestEmitter.EmitAggregateSourceTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Sql", "SourceRow", "MyPackage.Sql", "TargetAggregateRow",
            "AGG_ByRegion", "Region", "string", functions);

        Assert.Null(file);
    }

    [Fact]
    public void EmitAggregateSourceTest_ReturnsNull_WhenThereAreNoFunctions()
    {
        var file = ComponentTestEmitter.EmitAggregateSourceTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Sql", "SourceRow", "MyPackage.Sql", "TargetAggregateRow",
            "AGG_ByRegion", "Region", "string", []);

        Assert.Null(file);
    }

    [Fact]
    public void EmitSqlSourceTest_AssertsTheNameOnlyOnFact_AndTagsTheFullReadAsIntegration()
    {
        var file = ComponentTestEmitter.EmitSqlSourceTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Sql", "MyRow", "OLESRC_CustomerIds", "OLESRC_CustomerIds");

        Assert.Contains("var source = package.OLESRC_CustomerIds(uow);", file.Content);
        Assert.Contains("Assert.Equal(\"OLESRC_CustomerIds\", source.Name);", file.Content);
        Assert.Contains("[Trait(\"Category\", \"Integration\")]", file.Content);
        Assert.Contains("var rows = new List<MyRow>();", file.Content);
        // The .Name-only Fact still uses the Fake (no server needed at all); the Integration Fact
        // must use a REAL, already-begun UnitOfWork -- a FakeUnitOfWork's own bind token is always
        // the same hardcoded placeholder string, which sp_bindsession on any real server rejects
        // regardless of what server PackageHarness's DatabaseOptions points at (caught 2026-09-06
        // running this exact test against .\SQLFORPOC_2022 for the first time).
        Assert.Contains("var uow = harness.NewUnitOfWork();", file.Content);
        Assert.Contains("await using var uow = await harness.NewRealUnitOfWorkAsync();", file.Content);
    }

    [Fact]
    public void EmitExcelSourceTest_TakesNoUowArgument_AndTagsTheFullReadAsIntegration()
    {
        var file = ComponentTestEmitter.EmitExcelSourceTest(
            "MyPackage.Tests", "MyPackage", "MyPackage.Excel", "MyRow", "EXCEL_SRC_Drip", "EXCEL_SRC_Drip");

        Assert.Contains("var source = package.EXCEL_SRC_Drip();", file.Content);
        Assert.DoesNotContain("var uow = harness.NewUnitOfWork();", file.Content);
        Assert.Contains("Assert.Equal(\"EXCEL_SRC_Drip\", source.Name);", file.Content);
        Assert.Contains("[Trait(\"Category\", \"Integration\")]", file.Content);
    }
}
