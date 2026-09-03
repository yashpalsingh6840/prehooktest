namespace Etl.Core.Ssis;

/// <summary>
/// Declares a Flat File Source column's MaximumWidth from the .dtsx (e.g. State=2,
/// FirstName/LastName/City=50). Enforced by <c>CsvRowSource{TRow}</c> so the source-side
/// truncation checkpoint -- the one that actually fires, since no CSV value that passes it
/// can overflow a derived column downstream -- is reproduced, not just the derived one.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SsisWidthAttribute(int maxWidth) : Attribute
{
    public int MaxWidth { get; } = maxWidth;
}
