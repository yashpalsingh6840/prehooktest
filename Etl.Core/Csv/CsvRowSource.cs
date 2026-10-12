using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Etl.Core.Abstractions;
using Etl.Core.Ssis;
using Etl.Core.Storage;

namespace Etl.Core.Csv;

/// <summary>
/// A Flat File Source. CsvHelper-backed, CodePage-aware, and enforcing each column's
/// [SsisWidth] the way the .dtsx Flat File Source's own MaximumWidth does.
///
/// <para><b>Error-output routing</b> (<paramref name="divertErrorsToErrorRows"/>, default
/// <c>false</c>): a row that fails to convert, or overflows a declared width, is diverted into
/// <see cref="ErrorRows"/> instead of aborting the whole read -- the same buffer-then-flush
/// pattern this project's own Multicast/Conditional Split support already uses, since the error
/// row can only be known complete once reading finishes. Deliberately opt-in and defaulting to
/// <c>false</c>: every existing generated package (no error-output routing configured anywhere)
/// keeps today's exact behavior -- a bad row aborts the load loudly -- with zero risk of silently
/// swallowing data it used to fail on. Only a flow whose Flat File Source genuinely configures
/// <c>ErrorRowDisposition</c>/<c>TruncationRowDisposition</c> = RedirectRow on at least one main-
/// output column (see <c>PackagePlanner.ResolveFlatFileErrorOutput</c>) passes <c>true</c>.</para>
///
/// <para>Scoped to "any failing column routes the whole row to the error stream", not independent
/// per-column disposition -- CsvHelper's own <c>GetRecord&lt;TRow&gt;()</c> either succeeds or
/// throws for the WHOLE row, with no way to convert some columns and fail others within one call,
/// and the one evidenced real shape (a real client's MFDBUpdate.dtsx) configures every column of
/// its flow this way. A Data Flow Task mixing some RedirectRow columns with other FailComponent
/// columns is a different, currently-unevidenced shape.</para>
///
/// <para>The two <see cref="CsvErrorRow.ErrorCode"/> values below were measured against a real
/// dtexec run, not guessed (<c>Ssis.Extract.FixtureBuilder</c>'s "flat-file-error-probe" mode,
/// read back from <c>dbo.SyntheticFlatFileErrorProbeErrors</c>): a genuine type-conversion failure
/// (a non-numeric value in a DT_I4 column) produced <c>-1071607676</c> ("The data value cannot be
/// converted for reasons other than sign mismatch or data overflow."), and a genuine truncation
/// failure (a value exceeding its declared DT_WSTR width) produced <c>-1071607675</c> ("The data
/// was truncated.") -- both descriptions confirmed via a real
/// <c>IDTSComponentMetaData100.GetErrorDescription(int)</c> call, independent of execution.</para>
/// </summary>
public sealed class CsvRowSource<TRow> : IRowSource<TRow>
{
    /// <summary>Measured via a real dtexec run -- see this class's own doc comment.</summary>
    private const int ConversionFailureErrorCode = -1071607676;

    /// <summary>Measured via a real dtexec run -- see this class's own doc comment.</summary>
    private const int TruncationFailureErrorCode = -1071607675;

    private readonly CsvSourceOptions _options;
    private readonly ClassMap _classMap;
    private readonly IFileStore _fileStore;
    private readonly bool _divertErrorsToErrorRows;
    private readonly (PropertyInfo Property, int MaxWidth)[] _widthChecks;
    private readonly List<CsvErrorRow> _errorRows = [];

    public CsvRowSource(string name, CsvSourceOptions options, ClassMap classMap, IFileStore? fileStore = null, bool divertErrorsToErrorRows = false)
    {
        _fileStore = fileStore ?? LocalFileStore.Instance;
        CodePageSupport.EnsureRegistered();
        Name = name;
        _options = options;
        _classMap = classMap;
        _divertErrorsToErrorRows = divertErrorsToErrorRows;
        _widthChecks = BuildWidthChecks();
    }

    public string Name { get; }

    /// <summary>Every row diverted so far -- only ever non-empty when this instance was
    /// constructed with <c>divertErrorsToErrorRows: true</c>. Populated progressively while
    /// <see cref="ReadAsync"/> is enumerated; fully complete only once that enumeration has run to
    /// completion (the same timing <c>MulticastStep</c>'s own branches rely on for their own
    /// buffer-then-flush read).</summary>
    public IReadOnlyList<CsvErrorRow> ErrorRows => _errorRows;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!await _fileStore.ExistsAsync(_options.FilePath, ct))
            throw new FileNotFoundException($"{Name}: CSV source not found: {_options.FilePath}", _options.FilePath);

        var encoding = Encoding.GetEncoding(_options.CodePage);
        await using var stream = await _fileStore.OpenReadAsync(_options.FilePath, ct);
        using var streamReader = new StreamReader(stream, encoding);

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
            TRow? row = default;
            var divertedErrorCode = 0;
            var diverted = false;
            try
            {
                row = csv.GetRecord<TRow>();
                EnforceWidths(row, rowNumber);
            }
            catch (CsvHelperException) when (_divertErrorsToErrorRows)
            {
                divertedErrorCode = ConversionFailureErrorCode;
                diverted = true;
            }
            catch (SsisTruncationException) when (_divertErrorsToErrorRows)
            {
                divertedErrorCode = TruncationFailureErrorCode;
                diverted = true;
            }
            catch (CsvHelperException ex)
            {
                throw new InvalidOperationException(
                    $"{Name}: CSV parse error at row {rowNumber} (source line {csv.Context.Parser?.Row}): {ex.Message}", ex);
            }

            if (diverted)
            {
                // ErrorColumn is always 0 -- see CsvErrorRow.ErrorColumn's own doc comment.
                _errorRows.Add(new CsvErrorRow(csv.Context.Parser?.RawRecord ?? "", divertedErrorCode, 0));
                continue;
            }

            yield return row!;
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
