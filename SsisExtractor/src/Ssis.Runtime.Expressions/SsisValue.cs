using System.Globalization;

namespace Ssis.Runtime.Expressions;

/// <summary>
/// A typed SSIS value, including a typed NULL -- <c>NULL(DT_WSTR,10)</c> and
/// <c>NULL(DT_I4)</c> are different values even though both are "null", exactly as SSIS's own
/// <c>NULL(&lt;type&gt;)</c> literal requires a type argument. Getting this right matters:
/// operator type-checking (e.g. "+" between a string and a number is an error) has to work
/// even when one side is null, so the type has to survive nullness.
/// </summary>
public readonly struct SsisValue
{
    public SsisType Type { get; }
    public bool IsNull { get; }
    private readonly object? _raw;

    private SsisValue(SsisType type, object? raw, bool isNull)
    {
        Type = type;
        _raw = raw;
        IsNull = isNull;
    }

    public static SsisValue Null(SsisType type) => new(type, null, true);
    public static SsisValue OfString(string value, SsisType type = SsisType.WStr) => new(type, value, false);
    public static SsisValue OfInt(long value, SsisType type = SsisType.I4) => new(type, value, false);
    public static SsisValue OfDouble(double value, SsisType type = SsisType.R8) => new(type, value, false);
    public static SsisValue OfFloat(float value) => new(SsisType.R4, value, false);
    public static SsisValue OfDecimal(decimal value) => new(SsisType.Numeric, value, false);
    public static SsisValue OfBool(bool value) => new(SsisType.Bool, value, false);
    public static SsisValue OfDate(DateTime value, SsisType type = SsisType.DbTimeStamp) => new(type, value, false);

    public string AsString => _raw is string s ? s : throw new SsisExpressionError($"value of type {Type} is not a string");
    public long AsInt64 => _raw switch
    {
        long l => l,
        int i => i,
        _ => throw new SsisExpressionError($"value of type {Type} is not an integer"),
    };
    public double AsDouble => _raw switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        decimal m => (double)m,
        _ => throw new SsisExpressionError($"value of type {Type} is not a float"),
    };
    public decimal AsDecimal => _raw switch
    {
        decimal m => m,
        double d => (decimal)d,
        float f => (decimal)f,
        long l => l,
        int i => i,
        _ => throw new SsisExpressionError($"value of type {Type} is not numeric"),
    };
    public bool AsBool => _raw is bool b ? b : throw new SsisExpressionError($"value of type {Type} is not a boolean");
    public DateTime AsDate => _raw is DateTime d ? d : throw new SsisExpressionError($"value of type {Type} is not a date");

    /// <summary>
    /// The value's default string form -- used by (DT_WSTR,n)/(DT_STR,n) casts before the
    /// length check, and by the test generator to render an expected value into a C# literal.
    ///
    /// <b>Known, measured, deliberately-unreproduced gap:</b> the oracle corpus shows SSIS's
    /// (DT_WSTR,n) cast of a float/DT_NUMERIC produced by ARITHMETIC (not a plain cast or
    /// literal) sometimes pads to a fixed decimal-place count that varies by expression shape
    /// -- e.g. <c>5.0/2</c> renders as <c>"2.500000000000"</c> (12 decimals), <c>5.0/2.0</c>
    /// as <c>"2.500000"</c> (6 decimals), <c>2.0 * 1</c> as <c>"2.0"</c>, while a plain cast
    /// like <c>(DT_R8)"1e3"</c> renders as <c>"1000"</c> with no padding at all. This looks
    /// like SSIS's expression compiler tracking a "scale" through literals/arithmetic
    /// (similar to DT_NUMERIC's own precision/scale) and threading it into the cast -- an
    /// internal behaviour, not documented, and not worth reverse-engineering: none of this
    /// PoC's actual harvested expressions do float arithmetic before a string cast (the
    /// measured surface is string concat, UPPER, SUBSTRING, GETUTCDATE, and DT_WSTR casts --
    /// see CLAUDE.md). This method instead uses .NET's own shortest-round-trip formatting.
    /// It matches the corpus for every row NOT produced by float/numeric arithmetic (plain
    /// literals, plain casts, DT_NUMERIC via explicit cast) -- see
    /// <c>OracleCorpusTests.KnownDivergences</c> for the exact rows this does not reproduce,
    /// and why a Transformation rule doing float arithmetic before casting to string needs a
    /// human-reviewed golden test, not a generated one.
    /// </summary>
    public string ToDisplayString()
    {
        if (IsNull) throw new SsisExpressionError("cannot stringify a NULL value directly -- check IsNull first");
        return _raw switch
        {
            string s => s,
            bool b => b ? "True" : "False",
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            DateTime dt when Type == SsisType.DbDate => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // Measured (2026-09-15, added for DATEADD -- Phase 2 of the unsupported-component-
            // types plan): SSIS's own (DT_WSTR,n) cast of a DT_DBTIMESTAMP OMITS the fractional-
            // second suffix entirely when it is exactly zero -- "2020-01-01 00:00:00.000" casts
            // to "2020-01-01 00:00:00" (no trailing ".000000000"), while "...00.500" casts to
            // "2020-01-01 00:00:00.500000000" (9-digit zero-padded fraction), same as the
            // already-known "2020-02-29 13:45:00.123" -> "...123000000" row. A whole-second
            // DATEADD result (the real evidenced Microsoft.ExpressionTask shape,
            // DATEADD("Minute",-5,GETUTCDATE())) always has a zero fraction, so this was
            // previously untested by the corpus (no prior row exercised a raw, uncast date
            // value) and would otherwise render a spurious ".0000000" suffix no real SSIS output
            // ever has.
            DateTime dt when dt.Ticks % TimeSpan.TicksPerSecond == 0 =>
                dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff00", CultureInfo.InvariantCulture),
            _ => throw new SsisExpressionError($"no display form for {Type}"),
        };
    }
}
