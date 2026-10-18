namespace Etl.Core.Csv;

/// <summary>
/// One row a <see cref="CsvRowSource{TRow}"/> could not parse into <c>TRow</c> -- diverted here
/// instead of aborting the whole read, mirroring a real SSIS Flat File Source's own error-output
/// shape: a single raw-text column carrying the entire failed row's own source line, plus the two
/// system diagnostic columns every Flat File Source error output carries (confirmed via a real
/// object-model probe, <c>Ssis.Extract.FixtureBuilder</c>'s own "flat-file-error-probe" mode --
/// exactly 3 columns, regardless of how many real columns the main output declares: "Flat File
/// Source Error Output Column" (DT_TEXT), ErrorCode (DT_I4), ErrorColumn (DT_I4)).
/// </summary>
/// <param name="RawRow">The raw, unparsed source line -- <c>IParser.RawRecord</c>, captured
/// before type conversion is attempted, so it is available whether the row failed to convert at
/// all or merely overflowed one column's declared width. Includes the trailing row-delimiter
/// (CRLF) -- confirmed, not assumed, to be an EXACT match with real SSIS's own "Flat File Source
/// Error Output Column": a real dtexec probe against this same literal row measured
/// <c>DATALENGTH</c>=11 for a 9-character value ("abc,Carol"), the identical +2 bytes
/// <c>RawRecord</c> already includes here -- stripping it would have been "fixing" something
/// that was never broken.</param>
/// <param name="ErrorCode">Measured against a real dtexec run (see
/// <see cref="CsvRowSource{TRow}"/>'s own doc comment for the two values this maps onto) -- real
/// SSIS HRESULT-shaped error codes, not guessed. <c>ComponentMetaData.GetErrorDescription(int)</c>
/// resolves either one to its real human-readable text.</param>
/// <param name="ErrorColumn">Always <c>0</c> -- real SSIS resolves this to the failing column's
/// own internal pipeline LINEAGE ID (confirmed non-zero and column-specific via the same probe:
/// 7 and 10 for two different columns in one run), which has no equivalent concept in this
/// generated runtime at all. Reproducing a fake lineage-id-shaped number would be a guess this
/// tool's own "gaps not guesses" discipline refuses -- same reasoning, same value, as
/// <c>PackageGenerator.ResolveErrorRedirectSink</c>'s own identically-named limitation for the
/// destination-level (not source-level) error-redirect shape.</param>
public sealed record CsvErrorRow(string RawRow, int ErrorCode, int ErrorColumn);
