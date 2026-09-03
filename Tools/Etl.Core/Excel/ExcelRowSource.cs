using System.Runtime.CompilerServices;
using Etl.Core.Abstractions;
using Etl.Core.Csv;
using ExcelDataReader;

namespace Etl.Core.Excel;

/// <summary>
/// An Excel Source (<c>Microsoft.ExcelSource</c>, AccessMode=0/OpenRowset only -- a SqlCommand-
/// mode Excel Source is a separate, unevidenced generator gap, never guessed here). Owns its own
/// file handle, entirely separate from the package's load transaction (<see
/// cref="IRowSource{TRow}.ReadAsync"/> gets no <c>IUnitOfWork</c>/connection parameter to reuse)
/// -- the same separation <see cref="Etl.Core.Data.SqlRowSource{TRow}"/> already has from the
/// destination's transactional connection.
///
/// Reads via ExcelDataReader rather than the ACE/Jet OLE DB provider the real SSIS component
/// uses -- that provider is Windows-only and requires an install this rewrite deliberately does
/// not depend on. ExcelDataReader returns each cell already typed (double/string/DateTime/bool/
/// null) from the worksheet's own cell format, matching the real component's own observed
/// r8/wstr output typing closely enough for the one evidenced real shape (a worksheet with no
/// mixed-type columns); a column whose real SSIS output type is neither <c>double</c> nor
/// <c>string</c> is a separate, unevidenced gap.
/// </summary>
public sealed class ExcelRowSource<TRow>(string name, ExcelSourceOptions options, Func<IExcelDataReader, TRow> materialize) : IRowSource<TRow>
{
    public string Name => name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(options.FilePath))
            throw new FileNotFoundException($"{name}: Excel source not found: {options.FilePath}", options.FilePath);

        // ExcelReaderFactory's own default configuration calls Encoding.GetEncoding(1252) even
        // for a modern .xlsx workbook -- not built into .NET without this, same reason
        // CsvRowSource<TRow> already needs it for CodePage-driven text decoding.
        CodePageSupport.EnsureRegistered();

        using var stream = File.Open(options.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        await Task.Yield(); // ExcelDataReader has no async API of its own -- this justifies the async iterator, matching CsvRowSource's own genuine await point.

        // "DripEligibility$" (the .dtsx's own OpenRowset value, the ADO/Jet
        // worksheet-vs-named-range marker) vs. ExcelDataReader's own sheet name, "DripEligibility"
        // -- confirmed real by comparing the two against RBC_Demo_ETL's actual workbook.
        var worksheetName = options.WorksheetName.TrimEnd('$');
        var found = false;
        do
        {
            if (string.Equals(reader.Name, worksheetName, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        } while (reader.NextResult());

        if (!found)
            throw new InvalidOperationException($"{name}: worksheet '{options.WorksheetName}' was not found in '{options.FilePath}'");

        if (options.HasHeaderRow) reader.Read(); // discard the header row -- HDR=YES's own effect

        long rowNumber = options.HasHeaderRow ? 1 : 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            rowNumber++;
            TRow row;
            try
            {
                row = materialize(reader);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{name}: Excel parse error at worksheet row {rowNumber}: {ex.Message}", ex);
            }
            yield return row;
        }
    }
}
