using System.Runtime.CompilerServices;

using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Direct unit tests for <see cref="TransformTestEmitter"/>'s Data Conversion support (added
/// alongside the plain Derived-Column pilot) -- reach into a REAL fixture's already-parsed
/// pipeline/components rather than hand-building a <see cref="PipelineComponentSpec"/> graph, the
/// same discipline <c>TestFixtures</c>' own doc comment states: a hand-rolled spec might not
/// match what the extractor actually produces. <see cref="PackageGeneratorTests"/> already covers
/// the plain string-Derived-Column path end to end (through the whole generator); these tests
/// exercise this emitter directly so the Data-Conversion-specific branches don't need a full
/// package-generation round trip each.
/// </summary>
public class TransformTestEmitterTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    // tests/Ssis.Extract.Codegen.Tests -> tests/Ssis.Extract.Tests/Fixtures.
    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    private static PipelineComponentSpec Component(PipelineSpec pipeline, string name) =>
        pipeline.Components.Single(c => c.Name == name);

    [Fact]
    public void Emit_AssertsAllThreeColumns_ForADataConversionOnlyFlow_WithNoDerivedColumn()
    {
        // SyntheticDataConversion.dtsx: OLE DB Source -> Data Conversion (DCONV_Types:
        // CustomerIdText -> CustomerId_i4 DT_I4, SignupDateText -> SignupDate_dt DT_DBDATE) ->
        // OLE DB Destination, plus a plain passthrough "ID". No Derived Column anywhere -- this
        // is the "convertedByName only, computedByName empty" path.
        var package = LoadSyntheticFixture("SyntheticDataConversion.dtsx");
        var pipeline = TestFixtures.FindPipeline(package, "DFT_DataConversionDemo");
        var dataConversion = Component(pipeline, "DCONV_Types");
        var destination = Component(pipeline, "OLE DB Destination");

        var result = TransformTestEmitter.Emit(new TransformTestRequest(
            TestNamespace: "Test.Tests",
            TransformClassName: "TargetTransform",
            MappingNamespace: "Test.Mapping",
            RowTypeNamespace: "Test.Sql",
            RowTypeName: "TargetSqlRow",
            EntityNamespace: "Test.Model",
            EntityName: "Target",
            Pipeline: pipeline,
            DerivedColumns: null,
            DataConversions: [dataConversion],
            DestinationComponent: destination));

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("TargetTransformTests.cs", file.RelativePath);

        // The plain passthrough column.
        Assert.Contains("ID = 7,", file.Content);
        // Both converted columns' raw source properties, with a canonical, cleanly-parseable value.
        Assert.Contains("CustomerIdText = \"123\",", file.Content);
        Assert.Contains("SignupDateText = \"2020-06-15\",", file.Content);
        // Computed by literally invoking int.Parse/DateOnly.Parse on that same raw value, not
        // hand-encoded -- see RepresentativeDataConversion's own doc comment.
        Assert.Contains("// CustomerId_i4 <- Data Conversion (CustomerIdText -> i4)", file.Content);
        Assert.Contains("Assert.Equal((int?)123, result.CustomerId_i4);", file.Content);
        // The comment quotes the RAW extracted TargetDataType verbatim ("dbDate", not "dbdate") --
        // only the switch inside RepresentativeDataConversion/WrapDataConversion lowercases it.
        Assert.Contains("// SignupDate_dt <- Data Conversion (SignupDateText -> dbDate)", file.Content);
        Assert.Contains("Assert.Equal((DateOnly?)new DateOnly(2020, 6, 15), result.SignupDate_dt);", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_AssertsTheRemainingFourSupportedTargetTypes()
    {
        // SyntheticDataConversionTypes.dtsx (R8/BOOL/WSTR) + SyntheticDataConversionTypes2.dtsx
        // (I2/I8/DBTIMESTAMP) between them exercise every Data Conversion target type
        // TransformEmitter.WrapDataConversion itself supports -- run both through the same
        // emitter call to prove each representative/expected pairing, not just the two types
        // the first test above already covers (I4/DBDATE).
        AssertTypesFixture("SyntheticDataConversionTypes.dtsx", "DFT_Coerce", "DCONV_Types",
        [
            ("// ValR8 <- Data Conversion (R8Text -> r8)", "Assert.Equal((double?)7.5, result.ValR8);"),
            ("// ValBool <- Data Conversion (BoolText -> bool)", "Assert.Equal((bool?)true, result.ValBool);"),
            // WSTR's own representative ("Sample", 6 chars) fits under this fixture's declared
            // width (10) with no truncation -- proven not to guess a truncated value it doesn't
            // need, the truncating case itself is a one-line ternary covered by inspection.
            ("// ValWstr <- Data Conversion (WstrText -> wstr)", "Assert.Equal(\"Sample\", result.ValWstr);"),
        ]);

        AssertTypesFixture("SyntheticDataConversionTypes2.dtsx", "DFT_Coerce", "DCONV_Types2",
        [
            ("// Val_i2 <- Data Conversion (I2Text -> i2)", "Assert.Equal((short?)7, result.Val_i2);"),
            ("// Val_i8 <- Data Conversion (I8Text -> i8)", "Assert.Equal((long?)123456789012L, result.Val_i8);"),
            ("// Val_dt <- Data Conversion (TimestampText -> dbTimeStamp)",
             "Assert.Equal((DateTime?)new DateTime(2020, 6, 15, 12, 0, 0), result.Val_dt);"),
        ]);
    }

    private static void AssertTypesFixture(string dtsxFileName, string dftName, string dataConversionName,
        (string CommentPrefix, string Assertion)[] expected)
    {
        var package = LoadSyntheticFixture(dtsxFileName);
        var pipeline = TestFixtures.FindPipeline(package, dftName);
        var dataConversion = Component(pipeline, dataConversionName);
        var destination = Component(pipeline, "OLE DB Destination");

        var result = TransformTestEmitter.Emit(new TransformTestRequest(
            TestNamespace: "Test.Tests",
            TransformClassName: "TargetTransform",
            MappingNamespace: "Test.Mapping",
            RowTypeNamespace: "Test.Sql",
            RowTypeName: "TargetSqlRow",
            EntityNamespace: "Test.Model",
            EntityName: "Target",
            Pipeline: pipeline,
            DerivedColumns: null,
            DataConversions: [dataConversion],
            DestinationComponent: destination));

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        foreach (var (commentPrefix, assertion) in expected)
        {
            Assert.Contains(commentPrefix, file.Content);
            Assert.Contains(assertion, file.Content);
        }
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ReportsAGap_ForADerivedColumnExpression_ThatCrossReferencesADataConversionColumn()
    {
        // SyntheticDataConversionSplit.dtsx: DCONV_Types converts CustomerIdText/SignupDateText
        // into CustomerId_i4/SignupDate_dt; DER_ValidTenure's own TenureDays then computes
        // ISNULL(SignupDate_dt) ? -1 : DATEDIFF("dd", SignupDate_dt, GETDATE()) -- a Derived
        // Column expression referencing a Data-Conversion-PRODUCED column, not a raw row
        // property. This is the exact shape this emitter's own doc comment says it refuses to
        // guess at (that reference has no row property of its own to assign a representative
        // literal to) rather than silently emitting a broken row initializer.
        var package = LoadSyntheticFixture("SyntheticDataConversionSplit.dtsx");
        var pipeline = TestFixtures.FindPipeline(package, "DFT_DataConversionSplitDemo");
        var dataConversion = Component(pipeline, "DCONV_Types");
        var derivedColumn = Component(pipeline, "DER_ValidTenure");
        var destination = Component(pipeline, "OLE DB Destination");

        var result = TransformTestEmitter.Emit(new TransformTestRequest(
            TestNamespace: "Test.Tests",
            TransformClassName: "TargetTransform",
            MappingNamespace: "Test.Mapping",
            RowTypeNamespace: "Test.Sql",
            RowTypeName: "TargetSqlRow",
            EntityNamespace: "Test.Model",
            EntityName: "Target",
            Pipeline: pipeline,
            DerivedColumns: [derivedColumn],
            DataConversions: [dataConversion],
            DestinationComponent: destination));

        Assert.Contains(result.Gaps, g => g.Location == "Target.TenureDays"
            && g.Reason.Contains("cross-reference", StringComparison.Ordinal));

        var file = Assert.Single(result.Files);
        // The gapped column never appears in the generated assertions or the row initializer --
        // no compile-breaking reference to a nonexistent "SignupDate_dt" row property.
        Assert.DoesNotContain("TenureDays", file.Content);
        // But the columns this emitter CAN resolve for the same destination still get asserted:
        // the Data Conversion outputs directly, and the plain "ID" passthrough.
        Assert.Contains("Assert.Equal((int?)123, result.CustomerId_i4);", file.Content);
        Assert.Contains("Assert.Equal((DateOnly?)new DateOnly(2020, 6, 15), result.SignupDate_dt);", file.Content);
        Assert.Contains("ID = 7,", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    /// <summary>Pins the 2026-10-16 fix: every CLR type <c>SsisPipelineTypeMap</c> can actually
    /// produce must have a representative literal, or any starter-test emitter calling this
    /// (ComponentTestEmitter/TransformTestEmitter/RouterTestEmitter) crashes the whole `generate`
    /// run with an uncaught NotSupportedException the instant a real column resolves to one of
    /// them -- exactly what happened on a real client portfolio (HoldingsClassification_SSIS) via
    /// a Flat File Destination sink column. "byte[]" was already fixed in an earlier round; this
    /// closes the remaining gap (sbyte/byte/Guid/TimeSpan/TimeOnly/DateTimeOffset) comprehensively
    /// rather than one client file at a time.</summary>
    [Theory]
    [InlineData("sbyte")]
    [InlineData("byte")]
    [InlineData("Guid")]
    [InlineData("TimeSpan")]
    [InlineData("TimeOnly")]
    [InlineData("DateTimeOffset")]
    public void RepresentativeLiteral_HandlesEveryClrTypeSsisPipelineTypeMapCanProduce(string clrTypeName)
    {
        var literal = TransformTestEmitter.RepresentativeLiteral(clrTypeName);

        Assert.False(string.IsNullOrWhiteSpace(literal));
    }

    [Fact]
    public void RepresentativeLiteral_StillThrows_ForAGenuinelyUnknownClrType()
    {
        Assert.Throws<NotSupportedException>(() => TransformTestEmitter.RepresentativeLiteral("SomeFutureUnmappedType"));
    }

    /// <summary>Pins the 2026-10-16 fix: a real client flow had a plain passthrough column whose
    /// source buffer type (DateTime, from a Flat File Source reading it with no Data Conversion)
    /// disagreed with its destination's own declared type (string) -- this emitter's plain-
    /// passthrough branch used to blindly assume the row's own property type matched the
    /// DESTINATION's type for both the row initializer and the assert, producing a real client
    /// CS0029 ("string" literal into a row property CsvRowEmitter actually declared DateTime).
    /// TransformEmitter itself already gaps this exact shape (no coercion is evidenced for
    /// DateTime -&gt; string); this pilot test emitter now does the same rather than guess.</summary>
    [Fact]
    public void Emit_ReportsAGap_ForAPlainPassthroughColumn_WhoseSourceAndDestinationTypesDisagree()
    {
        var derivedColumn = new PipelineComponentSpec
        {
            RefId = "DER_Test",
            Name = "DER_Stamp",
            ComponentClassId = "Microsoft.DerivedColumn",
            Outputs =
            [
                new PipelineOutputSpec
                {
                    RefId = "DER_Test.Outputs[Derived Column Output]",
                    Name = "Derived Column Output",
                    Columns =
                    [
                        new PipelineOutputColumnSpec
                        {
                            RefId = "DER_Test.Outputs[Derived Column Output].Columns[LoadedAtUtc]",
                            Name = "LoadedAtUtc",
                            LineageId = "LIN_STAMP",
                            DataType = "dbtimestamp",
                            Expression = "GETUTCDATE()",
                            FriendlyExpression = "GETUTCDATE()",
                        },
                    ],
                },
            ],
        };

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
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[LoadedAtUtc]",
                            CachedName = "LoadedAtUtc",
                            CachedDataType = "dbtimestamp",
                            LineageId = "LIN_STAMP",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[LoadedAtUtc]",
                        },
                        new PipelineInputColumnSpec
                        {
                            RefId = "DST_Test.Inputs[OLE DB Destination Input].Columns[ArrivalDate]",
                            CachedName = "ArrivalDate",
                            // Buffered as DateTime (e.g. a Flat File Source's own output column
                            // override) while the real destination column is declared wstr --
                            // the exact disagreement TransformEmitter's own mismatch loop
                            // already gaps; no coercion is evidenced for this pairing.
                            CachedDataType = "dbtimestamp",
                            LineageId = "SRC.Outputs[Output].Columns[ArrivalDate]",
                            ExternalMetadataColumnId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[ArrivalDate]",
                        },
                    ],
                    ExternalMetadataColumns =
                    [
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[LoadedAtUtc]", Name = "LoadedAtUtc", DataType = "dbtimestamp" },
                        new PipelineExternalMetadataColumnSpec { RefId = "DST_Test.Inputs[OLE DB Destination Input].ExternalColumns[ArrivalDate]", Name = "ArrivalDate", DataType = "wstr", Length = 20 },
                    ],
                },
            ],
        };

        var request = new TransformTestRequest(
            TestNamespace: "Test.Tests",
            TransformClassName: "TargetTransform",
            MappingNamespace: "Test.Mapping",
            RowTypeNamespace: "Test.Sql",
            RowTypeName: "TargetSqlRow",
            EntityNamespace: "Test.Model",
            EntityName: "Target",
            Pipeline: new PipelineSpec { Components = [derivedColumn, destination] },
            DerivedColumns: [derivedColumn],
            DestinationComponent: destination);

        var result = TransformTestEmitter.Emit(request);

        Assert.Contains(result.Gaps, g => g.Location == "Target.ArrivalDate" && g.Reason.Contains("disagree", StringComparison.Ordinal));

        // The MISMATCHED column never reaches the row initializer or an assert at all -- no
        // broken `ArrivalDate = "Sample"` (a string literal into a row property CsvRowEmitter
        // actually declared DateTime) reaches the generated test file.
        var file = Assert.Single(result.Files);
        Assert.DoesNotContain("ArrivalDate", file.Content);
        Assert.Contains("Assert.Equal(new DateTime(2020, 6, 15, 12, 0, 0, DateTimeKind.Utc), result.LoadedAtUtc);", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
