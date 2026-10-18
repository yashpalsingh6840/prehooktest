using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen.Tests;

public class SampleDataEmitterTests
{
    /// <summary>Pins the 2026-10-16 fix: a real client connection manager declared
    /// <c>HeaderRowsToSkip &gt; 0</c> (a literal banner/control line before the real header) --
    /// <c>Etl.Core.Csv.CsvRowSource.ReadAsync</c> discards exactly <c>SkipRows</c> raw lines
    /// unconditionally before ever looking for a header, for the real file AND this synthesized
    /// one alike (both go through the identical <c>CsvSourceOptions.SkipRows</c>). This emitter
    /// used to write only <c>[header, dataLine, dataLine]</c> with no leading skip line(s) at
    /// all, so against a real reader configured with <c>SkipRows &gt; 0</c> the REAL header got
    /// silently discarded as a banner line and a representative DATA row (width-truncated
    /// "Sample" values) was parsed as the header instead -- a real client
    /// <c>CsvHelper.MissingFieldException</c> ("Field with name '&lt;real column&gt;' does not
    /// exist"), not a compile error, only visible at test-run time.</summary>
    [Fact]
    public void EmitCsv_WritesHeaderRowsToSkipPlaceholderLines_BeforeTheRealHeader()
    {
        var cm = new ConnectionManagerSpec
        {
            ObjectName = "CM_FF_Client",
            CreationName = "FLATFILE",
            Scope = "Package",
            WasRedacted = false,
            FlatFileFormat = new FlatFileFormatSpec
            {
                Format = "Delimited",
                ColumnNamesInFirstDataRow = true,
                HeaderRowsToSkip = 1,
                Columns =
                [
                    new FlatFileColumnSpec { ObjectName = "Rec", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 20 },
                    new FlatFileColumnSpec { ObjectName = "Amount", DataTypeRaw = 3, DataTypeName = "DT_I4" },
                ],
            },
        };

        var result = SampleDataEmitter.EmitCsv("Client_Extract", cm);

        Assert.NotNull(result);
        var lines = result!.File.Content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        // Exactly one skip line, THEN the real header, THEN two data rows -- never the header
        // landing on line 1 with no skip line ahead of it (what a SkipRows > 0 reader would
        // silently misread as a banner line, discarding the real header).
        Assert.Equal(4, lines.Length);
        Assert.Equal("SKIP", lines[0]);
        Assert.Equal("Rec,Amount", lines[1]);
        Assert.Equal("Sample,7", lines[2]);
        Assert.Equal("Sample,7", lines[3]);
    }

    [Fact]
    public void EmitCsv_WritesNoSkipLines_WhenHeaderRowsToSkipIsZero()
    {
        // No-regression check: the ordinary, far more common case (no banner line at all) is
        // unaffected by this fix -- still exactly [header, dataLine, dataLine].
        var cm = new ConnectionManagerSpec
        {
            ObjectName = "CM_FF_Plain",
            CreationName = "FLATFILE",
            Scope = "Package",
            WasRedacted = false,
            FlatFileFormat = new FlatFileFormatSpec
            {
                Format = "Delimited",
                ColumnNamesInFirstDataRow = true,
                HeaderRowsToSkip = 0,
                Columns = [new FlatFileColumnSpec { ObjectName = "Name", DataTypeRaw = 130, DataTypeName = "DT_WSTR", MaximumWidth = 20 }],
            },
        };

        var result = SampleDataEmitter.EmitCsv("Plain_Extract", cm);

        Assert.NotNull(result);
        var lines = result!.File.Content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Assert.Equal(["Name", "Sample", "Sample"], lines);
    }
}
