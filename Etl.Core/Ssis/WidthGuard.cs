namespace Etl.Core.Ssis;

/// <summary>
/// Reproduces the (DT_WSTR,n) cast with errorRowDisposition/truncationRowDisposition both set
/// to FailComponent: SSIS fails the row rather than silently truncating past a declared width.
/// This applies at TWO checkpoints, both real in the source .dtsx files: the Flat File Source's
/// own per-column MaximumWidth (e.g. State=2), and the Derived Column transform's output width
/// (e.g. EmployeeKey=20). String.Length is the right measure for both -- DT_WSTR width and
/// NVARCHAR(n) are both UTF-16 code units, exactly like string.Length (surrogate pairs count as 2
/// in all three).
/// </summary>
public static class WidthGuard
{
    /// <summary>Throws <see cref="SsisTruncationException"/> if <paramref name="value"/> exceeds <paramref name="declaredWidth"/>.</summary>
    public static string Wstr(string value, int declaredWidth, string columnName, long rowNumber)
    {
        if (value.Length > declaredWidth)
            throw new SsisTruncationException(columnName, declaredWidth, value, rowNumber);

        return value;
    }
}
