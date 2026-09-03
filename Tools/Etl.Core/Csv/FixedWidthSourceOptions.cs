namespace Etl.Core.Csv;

/// <summary>Mirrors a Flat File connection manager whose Format is FixedWidth/RaggedRight and
/// carries no column-NAME header (<c>ColumnNamesInFirstDataRow</c> false/absent) -- reading is
/// purely positional, so this has none of <see cref="CsvSourceOptions"/>'s delimiter/culture
/// settings.</summary>
public sealed class FixedWidthSourceOptions
{
    public required string FilePath { get; init; }

    /// <summary>SSIS DTS:CodePage="1252". Requires CodePagesEncodingProvider to be registered.</summary>
    public int CodePage { get; init; } = 1252;

    /// <summary>SSIS DTS:HeaderRowsToSkip -- a count of literal rows to discard before the first
    /// real data row, distinct from (and orthogonal to) column-name detection: this file's own
    /// row 1 is a control record ("CUVHDR ... HDR"), never a column-name header at all.</summary>
    public int SkipRows { get; init; } = 0;
}
