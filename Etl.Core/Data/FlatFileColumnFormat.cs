namespace Etl.Core.Data;

/// <summary>
/// One column's write-time layout for <see cref="FlatFileBulkSink{TEntity}"/> -- resolved from
/// the flat file connection manager's own <c>FlatFileColumn</c> entries (Ssis.Extract.Model's
/// <c>FlatFileFormatSpec</c>), not guessed. <see cref="FixedWidth"/> null means "delimited": the
/// raw value is written as-is, followed by <see cref="Delimiter"/>. A non-null
/// <see cref="FixedWidth"/> means the value is space-padded (or truncated, never erroring -- the
/// component carries no <c>usesDispositions</c>/truncation-disposition attribute at all, unlike
/// an OLE DB Destination's own input columns) to exactly that many characters before
/// <see cref="Delimiter"/> is written (typically empty for a fixed-width column; SSIS's own
/// FixedWidth format encodes the row terminator as a synthetic trailing DELIMITED column instead
/// -- see <see cref="FlatFileBulkSink{TEntity}"/>'s own doc comment).
/// </summary>
public sealed record FlatFileColumnFormat(string PropertyName, int? FixedWidth, string Delimiter);
