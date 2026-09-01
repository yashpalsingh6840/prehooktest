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
}
