using System.Globalization;

namespace Etl.Core.Csv;

/// <summary>Mirrors a Flat File connection manager's settings from a .dtsx file.</summary>
public sealed class CsvSourceOptions
{
    public required string FilePath { get; init; }

    /// <summary>SSIS DTS:CodePage="1252". Requires CodePagesEncodingProvider to be registered.</summary>
    public int CodePage { get; init; } = 1252;

    public bool HasHeaderRecord { get; init; } = true;

    /// <summary>SSIS DTS:HeaderRowsToSkip -- a count of literal rows to discard before header
    /// detection/data reading begins at all, independent of <see cref="HasHeaderRecord"/> (a file
    /// can skip N control rows and still have a real column-name header immediately after them,
    /// or no header at all). Zero for every connection manager that doesn't declare it.</summary>
    public int SkipRows { get; init; } = 0;

    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    /// <summary>
    /// SSIS DTS:ColumnDelimiter -- ",". for every column in both PoC packages, but a Flat File
    /// connection manager can declare any single- or multi-character delimiter (pipe, tab,
    /// semicolon), and an earlier version of this type hard-coded "," with no way to override it.
    /// </summary>
    public string Delimiter { get; init; } = ",";
}
