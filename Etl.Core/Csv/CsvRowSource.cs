using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Etl.Core.Abstractions;
using Etl.Core.Ssis;

namespace Etl.Core.Csv;

/// <summary>
/// A Flat File Source. CsvHelper-backed, CodePage-aware, and enforcing each column's
/// [SsisWidth] the way the .dtsx Flat File Source's own MaximumWidth does.
/// </summary>
public sealed class CsvRowSource<TRow> : IRowSource<TRow>
{
    private readonly CsvSourceOptions _options;
    private readonly ClassMap _classMap;
    private readonly (PropertyInfo Property, int MaxWidth)[] _widthChecks;

    public CsvRowSource(string name, CsvSourceOptions options, ClassMap classMap)
    {
        CodePageSupport.EnsureRegistered();
        Name = name;
        _options = options;
        _classMap = classMap;
        _widthChecks = BuildWidthChecks();
    }

    public string Name { get; }

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(_options.FilePath))
            throw new FileNotFoundException($"{Name}: CSV source not found: {_options.FilePath}", _options.FilePath);

        var encoding = Encoding.GetEncoding(_options.CodePage);
        using var streamReader = new StreamReader(_options.FilePath, encoding);

        for (var i = 0; i < _options.SkipRows; i++)
        {
            if (await streamReader.ReadLineAsync(ct) is null) yield break;
        }

        var csvConfig = new CsvConfiguration(_options.Culture)
        {
            HasHeaderRecord = _options.HasHeaderRecord,
            Delimiter = _options.Delimiter,
            // Left at CsvHelper's default (auto-detect \r\n vs \n): SSIS's hard-coded CRLF row
            // delimiter silently loads zero rows against an LF-only file. We accept either --
            // a deliberate, documented improvement -- paired with a zero-rows-read guard in the
            // load step so a genuinely empty/malformed file still fails loudly, just not silently.
        };

        using var csv = new CsvReader(streamReader, csvConfig);
        csv.Context.RegisterClassMap(_classMap);

        await csv.ReadAsync();
        if (_options.HasHeaderRecord) csv.ReadHeader();

        long rowNumber = 0;
        while (await csv.ReadAsync())
        {
            rowNumber++;
            TRow row;
            try
            {
                row = csv.GetRecord<TRow>();
            }
            catch (CsvHelperException ex)
            {
                throw new InvalidOperationException(
                    $"{Name}: CSV parse error at row {rowNumber} (source line {csv.Context.Parser?.Row}): {ex.Message}", ex);
            }

            EnforceWidths(row, rowNumber);
            yield return row;
        }
    }

    private void EnforceWidths(TRow row, long rowNumber)
    {
        foreach (var (property, maxWidth) in _widthChecks)
        {
            var value = (string?)property.GetValue(row) ?? string.Empty;
            WidthGuard.Wstr(value, maxWidth, $"{Name}.{property.Name}", rowNumber);
        }
    }

    private static (PropertyInfo, int)[] BuildWidthChecks() =>
        typeof(TRow).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (Property: p, Attr: p.GetCustomAttribute<SsisWidthAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Property, x.Attr!.MaxWidth))
            .ToArray();
}
