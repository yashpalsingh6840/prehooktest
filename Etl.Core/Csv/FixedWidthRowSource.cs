using System.Runtime.CompilerServices;
using System.Text;
using Etl.Core.Abstractions;

namespace Etl.Core.Csv;

/// <summary>
/// A Flat File Source whose connection manager has no column-name header at all -- SSIS's own
/// "FixedWidth"/"RaggedRight" formats, read purely by character position. Deliberately NOT
/// CsvHelper-backed: CsvHelper is a delimiter-based parser, and a genuinely fixed-width line
/// (e.g. "1         John Smith                    ...", no commas anywhere) is not something it
/// can split into named fields at all -- <see cref="CsvRowSource{TRow}"/>'s own attempt to do so
/// against a real file of this shape fails with a CsvHelper MissingFieldException, confirmed
/// against a real run before this type existed.
///
/// <see cref="FixedWidthSourceOptions.SkipRows"/> literal rows are discarded first (a control
/// record, not column names -- <see cref="FixedWidthColumnFormat"/>'s own doc comment), then
/// every remaining line is sliced per <paramref name="columns"/>' own widths (in the connection
/// manager's own column order) and handed to <paramref name="factory"/>. No trimming, no width
/// enforcement on read (SSIS's own Flat File Source has no read-side truncation-disposition
/// concept the way an OLE DB Destination's input columns do) -- verified against a real dtexec
/// run that this is exactly what SSIS itself produces.
/// </summary>
public sealed class FixedWidthRowSource<TRow>(
    string name, FixedWidthSourceOptions options, IReadOnlyList<FixedWidthColumnFormat> columns, Func<string[], TRow> factory)
    : IRowSource<TRow>
{
    public string Name { get; } = name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        CodePageSupport.EnsureRegistered();

        if (!File.Exists(options.FilePath))
            throw new FileNotFoundException($"{Name}: fixed-width source not found: {options.FilePath}", options.FilePath);

        var encoding = Encoding.GetEncoding(options.CodePage);
        using var reader = new StreamReader(options.FilePath, encoding);

        for (var i = 0; i < options.SkipRows; i++)
        {
            if (await reader.ReadLineAsync(ct) is null) yield break;
        }

        long rowNumber = options.SkipRows;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            rowNumber++;
            yield return factory(SliceRow(line, rowNumber));
        }
    }

    private string[] SliceRow(string line, long rowNumber)
    {
        var fields = new string[columns.Count];
        var position = 0;

        for (var i = 0; i < columns.Count; i++)
        {
            var width = columns[i].FixedWidth;
            if (width is int w)
            {
                if (position + w > line.Length)
                {
                    throw new InvalidOperationException(
                        $"{Name}: row {rowNumber} is shorter than expected -- column '{columns[i].PropertyName}' " +
                        $"needs characters {position}-{position + w} but the line is only {line.Length} characters long.");
                }

                fields[i] = line.Substring(position, w);
                position += w;
            }
            else
            {
                // The ragged column: whatever remains of the line. Only ever the last column in
                // every evidenced case, matching SSIS's own RaggedRight convention.
                fields[i] = position <= line.Length ? line[position..] : "";
            }
        }

        return fields;
    }
}
