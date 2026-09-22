namespace Etl.Core.Abstractions;

/// <summary>
/// What one <c>Microsoft.SCD</c> ("Slowly Changing Dimension") input column means to the component.
/// The numeric values are the raw <c>ColumnType</c> integers SSIS persists on each input column.
///
/// <para><b>This mapping was MEASURED, not read from documentation or reflected from an enum.</b>
/// <c>Microsoft.SCD</c> is implemented by a native <c>TxSCD.dll</c>, and its saved XML's own
/// <c>typeConverter="ColumnType"</c> attribute is resolved by the SSIS designer UI -- a scan of
/// every managed SSIS assembly on this machine found no such enum to reflect. So the meanings below
/// come from a real <c>dtexec</c> run of a fixture that wires all six SCD outputs to their own
/// observation tables, one source row per change kind, and reads back which table each row landed in
/// (see <c>SlowlyChangingDimensionStep{TRow,TKey}</c>'s own doc comment for the full measured rule
/// set and its evidence). The one real evidenced package's own companion <c>update.txt</c>
/// independently labels each of its changed columns with the SCD type it is meant to demonstrate,
/// and agrees with the measurement exactly.</para>
/// </summary>
public enum ScdColumnRole
{
    /// <summary>The business key -- what an incoming row is matched to the dimension by.
    /// Measured: <c>EmpId</c>, declared <c>ColumnType</c> 1, is what matched every incoming row to
    /// its dimension row, and an incoming key with no current dimension row was routed to
    /// <c>New Output</c>.</summary>
    BusinessKey = 1,

    /// <summary>A Type 1 ("changing") attribute -- a change overwrites the existing dimension row in
    /// place, keeping no history. Measured: changing <c>LastName</c> (<c>ColumnType</c> 2) alone
    /// routed the row to <c>Changing Attribute Updates Output</c>.</summary>
    Changing = 2,

    /// <summary>A Type 2 ("historical") attribute -- a change closes the existing dimension row and
    /// inserts a new one, keeping history. Measured: changing <c>Designation</c>
    /// (<c>ColumnType</c> 3) alone routed the row to <c>Historical Attribute Inserts
    /// Output</c>.</summary>
    Historical = 3,

    /// <summary>A fixed attribute -- one that is not supposed to change at all. Measured: changing
    /// <c>FirstName</c> (<c>ColumnType</c> 4) alone routed the row to <c>Fixed Attribute
    /// Output</c>, and a fixed change BEATS a simultaneous changing or historical change outright
    /// (two further probe rows, each with a fixed change plus one other kind, both went to
    /// <c>Fixed Attribute Output</c> only).</summary>
    Fixed = 4,
}
