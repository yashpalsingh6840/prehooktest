using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen.Tests;

public class FixedWidthRowReaderEmitterTests
{
    // Mirrors RBC_Demo_ETL's own CM_FF_CustomersFixed exactly (DTS:Format="RaggedRight",
    // DTS:HeaderRowsToSkip="1", no ColumnNamesInFirstDataRow) -- the real shape a real
    // generated run failed against with a CsvHelper MissingFieldException before this emitter
    // existed. See CustomersFixed.txt: row 1 is a control record ("CUVHDR ... HDR"), never
    // column names.
    private static ConnectionManagerSpec BuildCustomersFixedConnectionManager() => new()
    {
        ObjectName = "CM_FF_CustomersFixed",
        CreationName = "FLATFILE",
        Scope = "Package",
        WasRedacted = false,
        FlatFileFormat = new FlatFileFormatSpec
        {
            Format = "RaggedRight",
            HeaderRowsToSkip = 1,
            ColumnNamesInFirstDataRow = false,
            Columns =
            [
                new FlatFileColumnSpec { ObjectName = "CustomerID", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 10 },
                new FlatFileColumnSpec { ObjectName = "FullName", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 30 },
                new FlatFileColumnSpec { ObjectName = "Email", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 40 },
                new FlatFileColumnSpec
                {
                    ObjectName = "CountryCode", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 4,
                    ColumnType = "Delimited", ColumnDelimiterDecoded = "\r\n",
                },
            ],
        },
    };

    [Fact]
    public void IsFixedWidthWithoutHeader_RecognizesRaggedRightWithNoColumnNameHeader()
    {
        var cm = BuildCustomersFixedConnectionManager();
        Assert.True(FlatFileRuntimeShape.IsFixedWidthWithoutHeader(cm.FlatFileFormat));
    }

    [Theory]
    [InlineData(null)] // no FlatFileFormat at all
    public void IsFixedWidthWithoutHeader_FalseWhenFormatIsNull(FlatFileFormatSpec? format) =>
        Assert.False(FlatFileRuntimeShape.IsFixedWidthWithoutHeader(format));

    [Fact]
    public void IsFixedWidthWithoutHeader_FalseForAnOrdinaryDelimitedCsv()
    {
        var format = new FlatFileFormatSpec { Format = "Delimited", ColumnNamesInFirstDataRow = true };
        Assert.False(FlatFileRuntimeShape.IsFixedWidthWithoutHeader(format));
    }

    [Fact]
    public void IsFixedWidthWithoutHeader_FalseWhenColumnNamesInFirstDataRowIsTrue()
    {
        // A FixedWidth/RaggedRight file WITH a real column-name header is a different,
        // unevidenced shape -- stays on the ordinary ClassMapEmitter path (which will itself
        // report whatever gap that combination actually produces), not silently reinterpreted.
        var format = new FlatFileFormatSpec { Format = "RaggedRight", ColumnNamesInFirstDataRow = true };
        Assert.False(FlatFileRuntimeShape.IsFixedWidthWithoutHeader(format));
    }

    [Fact]
    public void BuildColumnPlans_ResolvesEachColumnsWidth_LastColumnRaggedAsNull()
    {
        var format = BuildCustomersFixedConnectionManager().FlatFileFormat!;

        var plans = FlatFileRuntimeShape.BuildColumnPlans(format);

        Assert.Equal(
        [
            new FixedWidthColumnPlan("CustomerID", 10),
            new FixedWidthColumnPlan("FullName", 30),
            new FixedWidthColumnPlan("Email", 40),
            new FixedWidthColumnPlan("CountryCode", null),
        ], plans);
    }

    [Fact]
    public void Emit_ReadsEachColumnByPosition_NotByName()
    {
        var cm = BuildCustomersFixedConnectionManager();

        var result = FixedWidthRowReaderEmitter.Emit("Package_Legacy.Csv", "DFT_FixedWidthImportCsvRow", cm);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Csv/DFT_FixedWidthImportCsvRowReader.cs", file.RelativePath);

        Assert.Contains("public static class DFT_FixedWidthImportCsvRowReader", file.Content);
        Assert.Contains("public static DFT_FixedWidthImportCsvRow Read(string[] fields) => new()", file.Content);
        Assert.Contains("CustomerID = fields[0],", file.Content);
        Assert.Contains("FullName = fields[1],", file.Content);
        Assert.Contains("Email = fields[2],", file.Content);
        Assert.Contains("CountryCode = fields[3],", file.Content);

        // Never CsvHelper's own .Name(...) lookup -- that's exactly what fails against a file
        // with no real column-name header.
        Assert.DoesNotContain(".Name(", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ReportsAGap_ForANonStringFixedWidthColumn()
    {
        var cm = new ConnectionManagerSpec
        {
            ObjectName = "CM_FF_Numeric",
            CreationName = "FLATFILE",
            Scope = "Package",
            WasRedacted = false,
            FlatFileFormat = new FlatFileFormatSpec
            {
                Format = "FixedWidth",
                ColumnNamesInFirstDataRow = false,
                Columns =
                [
                    new FlatFileColumnSpec { ObjectName = "Label", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 10 },
                    new FlatFileColumnSpec { ObjectName = "Amount", DataTypeRaw = 3, DataTypeName = "DT_I4", MaximumWidth = 10 },
                ],
            },
        };

        var result = FixedWidthRowReaderEmitter.Emit("X.Csv", "XRow", cm);

        var file = Assert.Single(result.Files); // Label still generates -- one bad column doesn't block the rest
        var gap = Assert.Single(result.Gaps);
        Assert.Contains("not string", gap.Reason);
        Assert.DoesNotContain("Amount", file.Content);
    }

    /// <summary>Pins the 2026-10-16 fix: a real client's RaggedRight layout had raw mainframe/
    /// copybook-style column ObjectNames that are not valid C# identifiers (one containing a
    /// comma + space, one a bare space). This emitter used to interpolate
    /// <c>column.ObjectName</c> VERBATIM into the reader's own object-initializer line -- two
    /// real column names colliding on one generated line produced invalid C#
    /// (CS0103 "name does not exist" x2 plus CS0747 "Invalid initializer member declarator"), or,
    /// for a single malformed name with nothing valid around it, a bare CS1003 syntax error.</summary>
    [Fact]
    public void Emit_SanitizesRawColumnNames_ThatAreNotValidCSharpIdentifiers()
    {
        var cm = new ConnectionManagerSpec
        {
            ObjectName = "CM_FF_Client",
            CreationName = "FLATFILE",
            Scope = "Package",
            WasRedacted = false,
            FlatFileFormat = new FlatFileFormatSpec
            {
                Format = "RaggedRight",
                ColumnNamesInFirstDataRow = false,
                Columns =
                [
                    new FlatFileColumnSpec { ObjectName = "CLICRD, OCC", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 10 },
                    new FlatFileColumnSpec { ObjectName = "MIGRATION DATE", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 8 },
                ],
            },
        };

        var result = FixedWidthRowReaderEmitter.Emit("ClientExtractEnt.Csv", "Client_ExtractCsvRow", cm);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        // The raw, malformed names never reach the generated file verbatim -- each collapses to
        // a single valid C# identifier (letters/digits/underscore only), matching exactly what
        // CsvRowEmitter's own PackageGenerator.SanitizeIdentifier would declare as the property.
        Assert.DoesNotContain("CLICRD, OCC", file.Content);
        Assert.DoesNotContain("MIGRATION DATE", file.Content);
        Assert.Contains("CLICRDOCC = fields[0],", file.Content);
        Assert.Contains("MIGRATIONDATE = fields[1],", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    /// <summary>A real client package feeds a Flat File Source's own OUTPUT column override into
    /// this emitter too (e.g. the CM declares DT_WSTR but the source reads it as something this
    /// emitter still resolves identically) -- confirms the resolver stays in sync with
    /// CsvRowEmitter's own identical override-aware resolution rather than diverging on a type
    /// this emitter happens to treat differently.</summary>
    [Fact]
    public void Emit_ProducesTheSameIdentifier_CsvRowEmitterWouldDeclare_ForACollidingColumnName()
    {
        var cm = new ConnectionManagerSpec
        {
            ObjectName = "CM_FF_Collide",
            CreationName = "FLATFILE",
            Scope = "Package",
            WasRedacted = false,
            FlatFileFormat = new FlatFileFormatSpec
            {
                Format = "RaggedRight",
                ColumnNamesInFirstDataRow = false,
                Columns =
                [
                    // Two raw names that sanitize to the SAME identifier -- CsvRowEmitter's
                    // shared MakeColumnIdentifierResolver disambiguates the second with "_2".
                    // Both emitters must walk the SAME column list in the SAME order and call
                    // identifierOf for the SAME subset of columns, or the reader's own LHS would
                    // silently stop matching the row type's own declared property.
                    new FlatFileColumnSpec { ObjectName = "A,B", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 5 },
                    new FlatFileColumnSpec { ObjectName = "A B", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 5 },
                ],
            },
        };

        var rowResult = CsvRowEmitter.Emit("X.Csv", "XRow", cm);
        var readerResult = FixedWidthRowReaderEmitter.Emit("X.Csv", "XRow", cm);

        Assert.Empty(rowResult.Gaps);
        Assert.Empty(readerResult.Gaps);
        var rowFile = Assert.Single(rowResult.Files);
        var readerFile = Assert.Single(readerResult.Files);

        Assert.Contains("public string AB { get; set; }", rowFile.Content);
        Assert.Contains("public string AB_2 { get; set; }", rowFile.Content);
        Assert.Contains("AB = fields[0],", readerFile.Content);
        Assert.Contains("AB_2 = fields[1],", readerFile.Content);

        CodeAssertions.AssertNoSyntaxErrors(rowFile.Content);
        CodeAssertions.AssertNoSyntaxErrors(readerFile.Content);
    }
}
