using System.Globalization;
using ClosedXML.Excel;

namespace Svk.Core;

/// <summary>
/// Writes a real, openable .xlsx workbook for an Excel Source touch point -- unlike a delimited
/// flat file or a SQL seed script, a renamed/reformatted CSV cannot stand in for this: Etl.Core's
/// own <c>ExcelRowSource{TRow}</c> reads via ExcelDataReader against genuine OOXML zip/XML
/// structure, so the sample data has to actually BE a workbook. Values are read back on the
/// generated side purely by ORDINAL POSITION (a raw <c>IDataRecord.GetName</c> throws for
/// ExcelDataReader, per <c>ExcelRowSource</c>'s own doc comment -- there is no by-name lookup to
/// fall back on), so columns here are written in exactly <paramref name="columns"/>' own order --
/// the same order the generated reader's own positional mapping expects.
/// </summary>
public static class ExcelSampleWriter
{
    public static void Write(string path, string worksheetName, bool hasHeaderRow, IReadOnlyList<ColumnSchema> columns, List<Dictionary<string, object?>> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SanitizeWorksheetName(worksheetName));

        var rowIndex = 1;
        if (hasHeaderRow)
        {
            for (var col = 0; col < columns.Count; col++)
            {
                sheet.Cell(rowIndex, col + 1).Value = columns[col].Name;
            }
            rowIndex++;
        }

        foreach (var row in rows)
        {
            for (var col = 0; col < columns.Count; col++)
            {
                SetCellValue(sheet.Cell(rowIndex, col + 1), row[columns[col].Name]);
            }
            rowIndex++;
        }

        workbook.SaveAs(path);
    }

    /// <summary>Excel worksheet names cap at 31 characters and reject <c>[ ] : * ? / \</c>
    /// outright -- ClosedXML itself THROWS on an illegal name rather than silently sanitizing it,
    /// so this has to happen before the name ever reaches <c>Worksheets.Add</c>.</summary>
    private static string SanitizeWorksheetName(string name)
    {
        var illegal = "[]:*?/\\";
        var cleaned = new string(name.Select(c => illegal.Contains(c) ? '_' : c).ToArray());
        if (cleaned.Length == 0) return "Sheet1";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    /// <summary>Every numeric shape is written as a plain double -- matching ExcelDataReader's own
    /// observed cell typing (double/string/DateTime/bool/null only, per <c>ExcelRowSource</c>'s
    /// doc comment), the same real component behavior this whole sample-data feature exists to
    /// reproduce, not the C# type the pipeline schema happens to name.</summary>
    private static void SetCellValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break; // leave blank -- a real NULL cell, no special marker needed
            case string s:
                cell.Value = s;
                break;
            case bool b:
                cell.Value = b;
                break;
            case DateOnly d:
                cell.Value = d.ToDateTime(TimeOnly.MinValue);
                break;
            case DateTime dt:
                cell.Value = dt;
                break;
            case DateTimeOffset dto:
                cell.Value = dto.DateTime;
                break;
            case TimeSpan ts:
                cell.Value = ts;
                break;
            case Guid g:
                cell.Value = g.ToString();
                break;
            case sbyte or byte or short or int or long or float or double or decimal:
                cell.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
            default:
                cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                break;
        }
    }
}
