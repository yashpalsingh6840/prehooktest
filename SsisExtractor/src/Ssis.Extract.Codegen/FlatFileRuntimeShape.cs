using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Codegen;

/// <summary>
/// One column's read-time layout for a fixed-width/RaggedRight Flat File Source -- the
/// codegen-side mirror of Etl.Core's own <c>FixedWidthColumnFormat</c> runtime type (this
/// project never references Etl.Core directly; it only emits source text referencing it).
/// </summary>
public sealed record FixedWidthColumnPlan(string PropertyName, int? FixedWidth);

/// <summary>
/// Distinguishes a Flat File Source whose connection manager has a genuine column-NAME header
/// (<see cref="ClassMapEmitter"/>'s ordinary CsvHelper <c>.Name(...)</c> path, unchanged) from
/// one that doesn't -- SSIS's own "FixedWidth"/"RaggedRight" formats read purely by character
/// position, which CsvHelper cannot do at all (it is a delimiter-based parser; a genuinely
/// fixed-width line has no delimiters to split on). Confirmed real, not guessed: RBC_Demo_ETL's
/// own CustomersFixed.txt (<c>DTS:Format="RaggedRight"</c>, <c>DTS:HeaderRowsToSkip="1"</c>, no
/// <c>ColumnNamesInFirstDataRow</c> at all) failed a real generated run with a CsvHelper
/// MissingFieldException before this type existed -- its first row is a control record
/// ("CUVHDR ... HDR"), never column names.
/// </summary>
public static class FlatFileRuntimeShape
{
    public static bool IsFixedWidthWithoutHeader(FlatFileFormatSpec? format) =>
        format is not null
        && format.Format is "FixedWidth" or "RaggedRight"
        && format.ColumnNamesInFirstDataRow != true;

    /// <summary>Per-column width, mirroring the exact per-column heuristic
    /// <c>PackageGenerator.ResolveFlatFileSink</c> already uses on the write side: a column
    /// with a declared width and no delimiter is a fixed-width slice; one with a delimiter (the
    /// ragged trailing column, in every evidenced case) is <see langword="null"/> -- "whatever
    /// remains of the line".</summary>
    public static IReadOnlyList<FixedWidthColumnPlan> BuildColumnPlans(FlatFileFormatSpec format) =>
        format.Columns
            .Select(c => new FixedWidthColumnPlan(
                c.ObjectName,
                c.MaximumWidth is int w && string.IsNullOrEmpty(c.ColumnDelimiterDecoded) ? w : null))
            .ToList();
}
