namespace Etl.Core.Ssis;

/// <summary>
/// Reproduces SSIS's errorRowDisposition="FailComponent" / truncationRowDisposition="FailComponent":
/// a value exceeding its declared DT_WSTR width must fail the load, never silently truncate.
/// </summary>
public sealed class SsisTruncationException(string columnName, int declaredWidth, string actualValue, long rowNumber)
    : Exception(
        $"Row {rowNumber}: '{columnName}' exceeds its declared width of {declaredWidth} " +
        $"(actual {actualValue.Length} chars): '{Preview(actualValue)}'")
{
    public string ColumnName { get; } = columnName;
    public int DeclaredWidth { get; } = declaredWidth;
    public int ActualLength { get; } = actualValue.Length;
    public long RowNumber { get; } = rowNumber;

    private static string Preview(string value) => value.Length <= 80 ? value : value[..80] + "...";
}
