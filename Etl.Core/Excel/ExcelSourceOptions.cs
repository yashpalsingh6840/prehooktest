namespace Etl.Core.Excel;

/// <summary>Mirrors an EXCEL connection manager + Excel Source component's own settings from a
/// .dtsx file -- see <see cref="ExcelRowSource{TRow}"/>'s own doc comment for the worksheet-name/
/// header-row semantics this drives.</summary>
public sealed class ExcelSourceOptions
{
    public required string FilePath { get; init; }

    /// <summary>The worksheet name, e.g. "Sheet1" -- any trailing "$" (the ADO/Jet
    /// worksheet-vs-named-range marker persisted in the .dtsx's own OpenRowset property, e.g.
    /// "DripEligibility$") is stripped by <see cref="ExcelRowSource{TRow}"/> itself, so either
    /// form works here.</summary>
    public required string WorksheetName { get; init; }

    /// <summary>SSIS's own Extended Properties=HDR=YES/NO -- true skips the worksheet's own
    /// first row when reading data rows.</summary>
    public bool HasHeaderRow { get; init; } = true;
}
