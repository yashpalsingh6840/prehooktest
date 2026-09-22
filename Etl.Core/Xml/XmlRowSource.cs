using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Etl.Core.Abstractions;

namespace Etl.Core.Xml;

/// <summary>
/// An XML Source (<c>Microsoft.XmlSourceAdapter</c>, AccessMode=0/a literal design-time file path
/// only -- a variable-driven path/document is a separate, unevidenced generator gap, never
/// guessed here). Owns its own file handle, entirely separate from the package's load transaction
/// (<see cref="IRowSource{TRow}.ReadAsync"/> gets no <c>IUnitOfWork</c>/connection parameter to
/// reuse) -- the same separation <see cref="Etl.Core.Data.SqlRowSource{TRow}"/>/
/// <see cref="Etl.Core.Excel.ExcelRowSource{TRow}"/> already have from the destination's
/// transactional connection.
///
/// <b>Deliberately scoped to one flat, single-repeating-element rowset</b> -- the one evidenced
/// real shape (<c>&lt;dataset&gt;&lt;record&gt;&lt;id&gt;1&lt;/id&gt;...&lt;/record&gt;...&lt;/dataset&gt;</c>):
/// every <see cref="XmlSourceOptions.RowElementName"/> match becomes one row, and each row's
/// direct child elements are read by NAME (via <c>materialize</c>, a per-column
/// <c>element.Element("name")?.Value</c> lookup, unlike <c>ExcelRowSource</c>'s own POSITIONAL
/// read -- <c>System.Xml.Linq</c> genuinely supports name-based lookup, so there is no reason to
/// copy Excel's own "no column-name API" limitation here). No nested/hierarchical XML (a
/// repeating element inside another repeating element) or multiple output rowsets are attempted
/// -- that shape is a named, unbuilt generator gap (see <c>PackagePlanner</c>'s own XML source
/// resolution), not silently mis-read here.
/// </summary>
public sealed class XmlRowSource<TRow>(string name, XmlSourceOptions options, Func<XElement, TRow> materialize) : IRowSource<TRow>
{
    public string Name => name;

    public async IAsyncEnumerable<TRow> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(options.FilePath))
            throw new FileNotFoundException($"{name}: XML source not found: {options.FilePath}", options.FilePath);

        XDocument document;
        using (var stream = File.Open(options.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        }

        long rowNumber = 0;
        foreach (var element in document.Descendants(options.RowElementName))
        {
            ct.ThrowIfCancellationRequested();
            rowNumber++;
            TRow row;
            try
            {
                row = materialize(element);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{name}: XML parse error at '{options.RowElementName}' #{rowNumber}: {ex.Message}", ex);
            }
            yield return row;
        }
    }
}
