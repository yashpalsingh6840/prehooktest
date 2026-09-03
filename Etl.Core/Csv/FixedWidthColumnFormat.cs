namespace Etl.Core.Csv;

/// <summary>
/// One column's read-time layout for <see cref="FixedWidthRowSource{TRow}"/> -- resolved from
/// the flat file connection manager's own <c>FlatFileColumn</c> entries, not guessed. A non-null
/// <see cref="FixedWidth"/> means the value is a fixed-length slice of the line, read verbatim
/// with no trimming -- confirmed against a real SSIS run that a fixed-width column lands padded
/// exactly as SSIS itself produced it (e.g. "1         " for a 10-wide column), not trimmed.
/// <see langword="null"/> means "ragged": the value is whatever remains of the line after every
/// preceding fixed-width column has consumed its own slice -- SSIS's own "RaggedRight" format,
/// where every column is fixed-width except the last.
/// </summary>
public sealed record FixedWidthColumnFormat(string PropertyName, int? FixedWidth);
