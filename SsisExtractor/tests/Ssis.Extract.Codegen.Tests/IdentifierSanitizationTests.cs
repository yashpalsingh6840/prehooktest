using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Covers <see cref="PackageGenerator.SanitizeIdentifier"/>/<see cref="PackageGenerator.MakeColumnIdentifierResolver"/>
/// directly, plus an end-to-end emitter round trip proving a real, space-containing SQL/pipeline
/// column name (e.g. a real WWI-schema-style "Last Cost Price") resolves to the SAME C#
/// identifier at every declaration AND reference site. Added 2026-09-18, closing a real
/// ~1181-error CS1002/CS1003 parse failure found regenerating sql-server-samples' own
/// DailyETLMain.dtsx (see CLAUDE.md's own dated section for the full account) -- before this
/// fix, every row/entity emitter in this project used a raw external column name verbatim as a
/// C# identifier, unconditionally.
/// </summary>
public class IdentifierSanitizationTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Theory]
    [InlineData("Last Cost Price", "LastCostPrice")]
    [InlineData("WWI Stock Item ID", "WWIStockItemID")]
    // A single leading/trailing/repeated space collapses the same way punctuation does.
    [InlineData("  Order   Date  ", "OrderDate")]
    // Confirms two genuinely different real WWI column names (one letter apart) stay distinct --
    // sanitizing must not accidentally collide them (the exact real-corpus warning this project's
    // own CLAUDE.md records: "Last Modifed When" vs "Last Modified When").
    [InlineData("Last Modifed When", "LastModifedWhen")]
    [InlineData("Last Modified When", "LastModifiedWhen")]
    public void SanitizeIdentifier_StripsPunctuationAndWhitespace(string raw, string expected)
    {
        Assert.Equal(expected, PackageGenerator.SanitizeIdentifier(raw));
    }

    [Theory]
    [InlineData("1099Amount", "_1099Amount")]
    [InlineData("2ndAttempt", "_2ndAttempt")]
    public void SanitizeIdentifier_GuardsALeadingDigit(string raw, string expected)
    {
        Assert.Equal(expected, PackageGenerator.SanitizeIdentifier(raw));
    }

    [Fact]
    public void SanitizeIdentifier_FallsBackToComponent_WhenNothingSurvivesSanitization()
    {
        Assert.Equal("Component", PackageGenerator.SanitizeIdentifier("!!!"));
        Assert.Equal("Component", PackageGenerator.SanitizeIdentifier(""));
        Assert.Equal("Component", PackageGenerator.SanitizeIdentifier("   "));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("namespace")]
    [InlineData("int")]
    [InlineData("string")]
    [InlineData("return")]
    public void SanitizeIdentifier_EscapesAReservedCSharpKeyword_WithAnAtPrefix(string keyword)
    {
        Assert.Equal("@" + keyword, PackageGenerator.SanitizeIdentifier(keyword));
    }

    [Theory]
    [InlineData("var")] // contextual keyword, legal as a plain identifier
    [InlineData("async")]
    [InlineData("Class")] // case-sensitive -- not a keyword
    public void SanitizeIdentifier_DoesNotEscapeAContextualKeywordOrDifferentCasing(string name)
    {
        Assert.Equal(name, PackageGenerator.SanitizeIdentifier(name));
    }

    [Fact]
    public void MakeColumnIdentifierResolver_IsPureAndDeterministic_ForANonCollidingList()
    {
        var resolver = PackageGenerator.MakeColumnIdentifierResolver();
        Assert.Equal("LastCostPrice", resolver("Last Cost Price"));
        Assert.Equal("WWIStockItemID", resolver("WWI Stock Item ID"));
        Assert.Equal("ID", resolver("ID"));
    }

    [Fact]
    public void MakeColumnIdentifierResolver_MemoizesTheSameRawName()
    {
        var resolver = PackageGenerator.MakeColumnIdentifierResolver();
        var first = resolver("Last Cost Price");
        var second = resolver("Last Cost Price");
        Assert.Equal(first, second);
        Assert.Equal("LastCostPrice", first);
    }

    [Fact]
    public void MakeColumnIdentifierResolver_DisambiguatesTwoDifferentRawNames_ThatSanitizeTheSame()
    {
        // "Last Cost Price" and "Last-Cost-Price" both sanitize to "LastCostPrice" -- a genuine
        // collision between two DIFFERENT raw names, unevidenced in any real corpus so far, but a
        // correct general-purpose resolver must not silently produce a duplicate identifier.
        var resolver = PackageGenerator.MakeColumnIdentifierResolver();
        var first = resolver("Last Cost Price");
        var second = resolver("Last-Cost-Price");
        Assert.Equal("LastCostPrice", first);
        Assert.Equal("LastCostPrice_2", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void MakeColumnIdentifierResolver_ThirdCollision_GetsTheNextSuffix()
    {
        var resolver = PackageGenerator.MakeColumnIdentifierResolver();
        Assert.Equal("Foo", resolver("Foo"));
        Assert.Equal("Foo_2", resolver("Foo "));
        Assert.Equal("Foo_3", resolver("Foo!"));
    }

    /// <summary>Hand-built, mirroring TransformEmitterTests' own established
    /// PipelineComponentSpec-construction pattern -- a plain passthrough destination column whose
    /// raw external/pipeline name has a literal space, proving TransformEmitter's own
    /// destination-side (LHS) AND source-side (row.{X}) identifiers both sanitize, and agree with
    /// each other, without needing a real .dtsx fixture for this one narrow case.</summary>
    [Fact]
    public void TransformEmitter_ResolvesAPlainPassthroughColumn_WithASpaceContainingRawName()
    {
        var destination = new PipelineComponentSpec
        {
            RefId = "DST_Test",
            Name = "OLE DB Destination",
            ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs =
            [
                new PipelineInputSpec
                {
                    RefId = "DST_Test.Inputs[OLE DB Destination Input]",
                    Name = "OLE DB Destination Input",
                    Columns =
                    [
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[Last Cost Price]",
                            CachedName = "Last Cost Price",
                            CachedDataType = "numeric",
                            LineageId = "SRC.Outputs[Output].Columns[Last Cost Price]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[Last Cost Price]",
                        },
                    ],
                    ExternalMetadataColumns =
                    [
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[Last Cost Price]", Name = "Last Cost Price", DataType = "numeric", Precision = 12, Scale = 2 },
                    ],
                },
            ],
        };
        var pipeline = new PipelineSpec { Components = [destination] };

        var request = new TransformRequest(
            "Generated.Mapping", "TargetTransform",
            "Generated.Sql", "TargetSqlRow",
            "Generated.Model", "Target",
            "Generated.Ssis",
            pipeline, [], destination);

        var result = TransformEmitter.Emit(request);

        Assert.Empty(result.Result.Gaps);
        var file = Assert.Single(result.Result.Files);
        Assert.Contains("LastCostPrice = row.LastCostPrice,", file.Content);
        Assert.DoesNotContain("Last Cost Price", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    /// <summary>The real, checked-in fixture (SyntheticIdentifierSanitization.dtsx, built via the
    /// SSIS object model against a real "Last Cost Price" column, the same shape as
    /// sql-server-samples/wwi-ssis/DailyETLMain.dtsx's own real WWI columns) -- proves
    /// SqlRowEmitter, SqlRowReaderEmitter, EntityEmitter, and DbContextEmitter ALL independently
    /// agree on the sanitized identifier for the SAME raw column, end to end, not just in
    /// isolation. Generated/built/run against .\SQLFORPOC_2022 separately (see CLAUDE.md); this
    /// test is the fast, no-database-needed regression guard for the same fixture.</summary>
    [Fact]
    public void RealFixture_EveryEmitterAgreesOnTheSanitizedIdentifier_ForASpaceContainingColumn()
    {
        var package = LoadSyntheticFixture("SyntheticIdentifierSanitization.dtsx");
        var source = TestFixtures.FindComponent(package, "Microsoft.OLEDBSource", "OLE DB Source");
        var destination = TestFixtures.FindComponent(package, "Microsoft.OLEDBDestination", "OLE DB Destination");

        var sqlRow = SqlRowEmitter.Emit("Generated.Sql", "TargetSqlRow", source);
        Assert.Empty(sqlRow.Gaps);
        var sqlRowFile = Assert.Single(sqlRow.Files);
        Assert.Contains("public decimal LastCostPrice { get; set; }", sqlRowFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(sqlRowFile.Content);

        var sqlRowReader = SqlRowReaderEmitter.Emit("Generated.Sql", "TargetSqlRow", source);
        var readerFile = Assert.Single(sqlRowReader.Files);
        // The real SQL SELECT's own column name -- a LITERAL lookup, must stay raw.
        Assert.Contains("reader.GetOrdinal(\"Last Cost Price\")", readerFile.Content);
        // The assignment TARGET -- an identifier, must be sanitized.
        Assert.Contains("LastCostPrice = reader.GetFieldValue<decimal>(", readerFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(readerFile.Content);

        var entity = EntityEmitter.Emit("Generated.Model", "Target", destination);
        Assert.Empty(entity.Gaps);
        var entityFile = Assert.Single(entity.Files);
        Assert.Contains("public decimal LastCostPrice { get; set; }", entityFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(entityFile.Content);

        var primaryKey = new Ssis.Extract.Model.Analysis.PrimaryKeyCandidateSpec
        {
            PackageName = "SyntheticIdentifierSanitization",
            DataFlowTaskPath = "DFT_Load",
            DestinationComponentName = "OLE DB Destination",
            DestinationComponentRefId = destination.RefId,
            Columns = ["ID"],
            Confidence = "NamingConvention",
            Reason = "test",
        };
        var dbContext = DbContextEmitter.Emit("Generated.Model", "TargetDbContext",
            [new DbContextEmitter.TableSpec("Target", destination, primaryKey)]);
        Assert.Empty(dbContext.Gaps);
        var dbContextFile = Assert.Single(dbContext.Files);
        // The identifier used in the lambda AND the real DB column name preserved via HasColumnName.
        Assert.Contains("entity.Property(e => e.LastCostPrice).HasColumnName(\"Last Cost Price\")", dbContextFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(dbContextFile.Content);
    }
}
