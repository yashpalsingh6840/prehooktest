namespace Ssis.Runtime.Expressions;

/// <summary>
/// The subset of SSIS's DTS data types this evaluator actually needs -- one entry per
/// <c>DT_*</c> cast name observed in the ground-truth oracle
/// (<c>tests/Ssis.Runtime.Expressions.Tests/Fixtures/ssis-expression-oracle.tsv</c>) or
/// producible by a literal. Not the full SSIS type system (no DT_I1/DT_UI*/DT_CY/DT_GUID/
/// DT_IMAGE/etc.) -- add a case here only once a real expression needs it, per the same
/// "measure, don't anticipate" discipline as the rest of this tool.
/// </summary>
public enum SsisType
{
    WStr,
    Str,
    I2,
    I4,
    I8,
    R4,
    R8,
    Numeric,
    Bool,
    DbTimeStamp,
    DbDate,
}

public static class SsisTypes
{
    public static bool IsString(SsisType t) => t is SsisType.WStr or SsisType.Str;

    public static bool IsIntegral(SsisType t) => t is SsisType.I2 or SsisType.I4 or SsisType.I8;

    public static bool IsFloat(SsisType t) => t is SsisType.R4 or SsisType.R8;

    /// <summary>Integral, float, or DT_NUMERIC -- anything arithmetic operators accept.</summary>
    public static bool IsNumeric(SsisType t) => IsIntegral(t) || IsFloat(t) || t == SsisType.Numeric;

    public static bool IsDate(SsisType t) => t is SsisType.DbTimeStamp or SsisType.DbDate;

    /// <summary>
    /// The result type of a numeric binary operator between two numeric types, promoting
    /// integral &lt; float &lt; DT_NUMERIC (decimal) -- e.g. I4 + R8 promotes to R8. Not
    /// itself measured against the oracle (the corpus only checks resulting VALUES, not
    /// their SsisType), but the ranking is the ordinary "widest wins" rule and only matters
    /// internally for which .NET numeric kind carries the arithmetic.
    /// </summary>
    public static SsisType Promote(SsisType a, SsisType b)
    {
        if (a == SsisType.Numeric || b == SsisType.Numeric) return SsisType.Numeric;
        if (IsFloat(a) || IsFloat(b)) return a == b ? a : SsisType.R8;
        // both integral: widen to the larger of the two, defaulting to I4 (SSIS's default
        // integer literal type) when neither is I8.
        if (a == SsisType.I8 || b == SsisType.I8) return SsisType.I8;
        return SsisType.I4;
    }

    /// <summary>Parses a cast header's type name, e.g. "DT_WSTR" -> <see cref="SsisType.WStr"/>. Case-insensitive (SSIS expressions are).</summary>
    public static SsisType Parse(string castName) => castName.ToUpperInvariant() switch
    {
        "DT_WSTR" => SsisType.WStr,
        "DT_STR" => SsisType.Str,
        "DT_I2" => SsisType.I2,
        "DT_I4" => SsisType.I4,
        "DT_I8" => SsisType.I8,
        "DT_R4" => SsisType.R4,
        "DT_R8" => SsisType.R8,
        "DT_NUMERIC" => SsisType.Numeric,
        "DT_BOOL" => SsisType.Bool,
        "DT_DBTIMESTAMP" => SsisType.DbTimeStamp,
        "DT_DBDATE" => SsisType.DbDate,
        _ => throw new SsisExpressionError($"unsupported cast type '{castName}' -- not in the measured oracle corpus, add it there first"),
    };
}
