namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits one package-scoped <c>Ssis/SsisFn.cs</c> -- only the SSIS expression-language
/// functions that package's Derived Column expressions actually call (see
/// <paramref name="functionsUsed"/> in <see cref="Emit"/>), each oracle-verified against
/// <c>tests/Ssis.Runtime.Expressions.Tests/Fixtures/ssis-expression-oracle.tsv</c> rather
/// than hand-guessed.
///
/// This reproduces, function for function and comment for comment, the hand-written
/// <c>D:\PoC\SSIS_Rewrite\src\LoadEmployees\Ssis\SsisFn.cs</c> /
/// <c>...\LoadReferenceData\Ssis\SsisFn.cs</c> -- those were written by hand first, before
/// this emitter existed, specifically to BE the target shape a generator should reproduce
/// (see CLAUDE.md's "ssis-rewrite-skeleton-decisions" note). Deliberately package-scoped, not
/// shared <c>Etl.Core</c> plumbing: see that same note's "SSIS semantics do NOT go in the
/// hand-written Core Library" line.
/// </summary>
public static class SsisFnEmitter
{
    public static EmitResult Emit(string ns, IReadOnlySet<string> functionsUsed)
    {
        if (functionsUsed.Count == 0)
            return new EmitResult([], []);

        var bodies = new List<List<string>>();
        if (functionsUsed.Contains("Upper")) bodies.Add(UpperBody());
        if (functionsUsed.Contains("Trim")) bodies.Add(TrimBody());
        if (functionsUsed.Contains("Substring")) bodies.Add(SubstringBody());
        if (functionsUsed.Contains("FindString")) bodies.Add(FindStringBody());
        if (functionsUsed.Contains("DateDiffDays")) bodies.Add(DateDiffDaysBody());
        if (functionsUsed.Contains("Str")) bodies.Add(StrBody());
        if (functionsUsed.Contains("ToNullableI4")) bodies.Add(ToNullableI4Body());
        if (functionsUsed.Contains("ToNullableDate")) bodies.Add(ToNullableDateBody());
        if (functionsUsed.Contains("NarrowR8ToI4")) bodies.Add(NarrowR8ToI4Body());
        if (functionsUsed.Contains("NarrowI8ToI4")) bodies.Add(NarrowI8ToI4Body());
        if (functionsUsed.Contains("NarrowNumericToI4")) bodies.Add(NarrowNumericToI4Body());
        if (functionsUsed.Contains("NarrowR4ToI4")) bodies.Add(NarrowR4ToI4Body());
        if (functionsUsed.Contains("ParseWstrToI4")) bodies.Add(ParseWstrToI4Body());
        if (functionsUsed.Contains("ToNullableR8")) bodies.Add(ToNullableR8Body());
        if (functionsUsed.Contains("ToNullableBool")) bodies.Add(ToNullableBoolBody());
        if (functionsUsed.Contains("ToNullableI2")) bodies.Add(ToNullableI2Body());
        if (functionsUsed.Contains("ToNullableI8")) bodies.Add(ToNullableI8Body());
        if (functionsUsed.Contains("ToNullableDateTime")) bodies.Add(ToNullableDateTimeBody());
        if (functionsUsed.Contains("ToWstr")) bodies.Add(ToWstrBody());

        var lines = new List<string>();
        if (functionsUsed.Contains("Str") || functionsUsed.Contains("ToNullableI4") || functionsUsed.Contains("ToNullableDate") || functionsUsed.Contains("ToNullableR8") || functionsUsed.Contains("ParseWstrToI4")
            || functionsUsed.Contains("ToNullableI2") || functionsUsed.Contains("ToNullableI8") || functionsUsed.Contains("ToNullableDateTime"))
        {
            lines.Add("using System.Globalization;");
            lines.Add("");
        }
        lines.Add($"namespace {ns};");
        lines.Add("");
        lines.Add("internal static class SsisFn");
        lines.Add("{");

        for (var i = 0; i < bodies.Count; i++)
        {
            lines.AddRange(bodies[i]);
            if (i < bodies.Count - 1) lines.Add("");
        }

        lines.Add("}");

        return new EmitResult([new GeneratedFile("Ssis/SsisFn.cs", Rendering.JoinLines(lines))], []);
    }

    private static List<string> UpperBody() =>
    [
        "    /// <summary>UPPER() -- always invariant. A plain ToUpper() would map 'i' -> 'İ' under tr-TR.</summary>",
        "    public static string Upper(string value) => value.ToUpperInvariant();",
    ];

    private static List<string> SubstringBody() =>
    [
        "    /// <summary>",
        "    /// SUBSTRING(expression, start, length), 1-based like SSIS/T-SQL. start &lt; 1 is a hard",
        "    /// error in real SSIS, not a clamp to the start of the string. start beyond the end of",
        "    /// the string clamps to \"\" instead of erroring.",
        "    /// </summary>",
        "    public static string Substring(string value, int oneBasedStart, int length)",
        "    {",
        "        if (oneBasedStart < 1)",
        "            throw new ArgumentOutOfRangeException(nameof(oneBasedStart), oneBasedStart,",
        "                \"SSIS SUBSTRING errors when start < 1; it does not clamp to the start of the string.\");",
        "",
        "        if (length <= 0 || oneBasedStart > value.Length) return string.Empty;",
        "",
        "        var zeroBasedStart = oneBasedStart - 1;",
        "        var take = Math.Min(length, value.Length - zeroBasedStart);",
        "        return value.Substring(zeroBasedStart, take);",
        "    }",
    ];

    private static List<string> TrimBody() =>
    [
        "    /// <summary>TRIM() -- matches .NET's Trim() exactly per the oracle corpus (docs/gate2-schema.md).</summary>",
        "    public static string Trim(string value) => value.Trim();",
    ];

    private static List<string> FindStringBody() =>
    [
        "    /// <summary>",
        "    /// FINDSTRING(expression, searchString, occurrence), 1-based like SSIS. occurrence &lt; 1",
        "    /// is a hard error, not a clamp. An empty search string never matches -- always returns",
        "    /// 0, regardless of occurrence. Matches may overlap: FindString(\"abcabc\", \"bc\", 2) == 5.",
        "    /// </summary>",
        "    public static int FindString(string value, string search, int occurrence)",
        "    {",
        "        if (occurrence < 1)",
        "            throw new ArgumentOutOfRangeException(nameof(occurrence), occurrence,",
        "                \"SSIS FINDSTRING errors when occurrence < 1.\");",
        "",
        "        if (search.Length == 0) return 0;",
        "",
        "        var searchFrom = 0;",
        "        var index = -1;",
        "        for (var count = 0; count < occurrence; count++)",
        "        {",
        "            index = value.IndexOf(search, searchFrom, StringComparison.Ordinal);",
        "            if (index < 0) return 0;",
        "            searchFrom = index + 1;",
        "        }",
        "        return index + 1;",
        "    }",
    ];

    private static List<string> DateDiffDaysBody() =>
    [
        "    /// <summary>",
        "    /// DATEDIFF(\"dd\", start, end) -- the only date part measured against the real SSIS",
        "    /// evaluator. Counts calendar-DAY-BOUNDARY crossings, exactly like T-SQL's own",
        "    /// DATEDIFF(\"dd\",...), NOT elapsed 24-hour periods -- measured: 23:00 to next day",
        "    /// 01:00 (2 hours elapsed) is 1, not 0. Sign convention is end - start.",
        "    /// </summary>",
        "    public static int DateDiffDays(DateTime start, DateTime end) => (end.Date - start.Date).Days;",
        "",
        "    /// <summary>",
        "    /// Overload for a DATE-typed SQL column reference, which this generator maps to",
        "    /// System.DateOnly (SqlRowReaderEmitter/EntityEmitter's own convention) rather than",
        "    /// DateTime -- the real evidenced call, DATEDIFF(\"dd\",SignupDate,GETDATE()), mixes a",
        "    /// DATE column (start) with GETDATE()/ctx.LoadedAtUtc (end, always DateTime). Same",
        "    /// day-boundary-crossing semantics as the DateTime overload above.",
        "    /// </summary>",
        "    public static int DateDiffDays(DateOnly start, DateTime end) => (end.Date - start.ToDateTime(TimeOnly.MinValue)).Days;",
    ];

    private static List<string> StrBody() =>
    [
        "    /// <summary>(DT_WSTR,n) cast of an integer -- invariant, no thousands separator.</summary>",
        "    public static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);",
    ];

    private static List<string> ToNullableI4Body() =>
    [
        "    /// <summary>",
        "    /// Data Conversion to DT_I4 with both dispositions IgnoreFailure -- the only",
        "    /// disposition combination this generator supports (see DataConvertPayload's own doc",
        "    /// comment). Measured empirically against a real dtexec run: a NULL/empty/",
        "    /// non-numeric source value never fails or drops the row, it just yields NULL here;",
        "    /// a valid value surrounded by whitespace still parses (locale-neutral, matching SSIS's",
        "    /// own FastParse=false behavior observed on the same run).",
        "    /// </summary>",
        "    public static int? ToNullableI4(string? value) =>",
        "        !string.IsNullOrEmpty(value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)",
        "            ? result",
        "            : null;",
    ];

    private static List<string> ToNullableDateBody() =>
    [
        "    /// <summary>",
        "    /// Data Conversion to DT_DBDATE with both dispositions IgnoreFailure -- same",
        "    /// empirically-measured semantics as ToNullableI4 above (NULL on any failure, never a",
        "    /// dropped row or thrown exception).",
        "    /// </summary>",
        "    public static DateOnly? ToNullableDate(string? value) =>",
        "        !string.IsNullOrEmpty(value) && DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var result)",
        "            ? result",
        "            : null;",
    ];

    private static List<string> NarrowR8ToI4Body() =>
    [
        "    /// <summary>",
        "    /// A plain passthrough column whose source buffer is float (DT_R8) but whose",
        "    /// destination column is int (DT_I4), with no explicit Data Conversion component --",
        "    /// the real shape RBC_Demo_ETL's own DFT_ExcelImport has (CustomerID: r8 from the",
        "    /// Excel worksheet, i4 at the destination table). SSIS's own OLE DB Destination",
        "    /// accepts this pairing and performs the narrowing itself at insert time; measured",
        "    /// empirically against a real dtexec run (synthetic-numeric-coercion-tables.sql):",
        "    /// rounds to the NEAREST integer, ties-to-EVEN -- matches .NET's own default",
        "    /// Math.Round midpoint rule exactly (2.5 -> 2, 3.5 -> 4, 4.5 -> 4, -3.5 -> -4). An",
        "    /// out-of-range result throws OverflowException (checked) rather than silently",
        "    /// wrapping -- a deliberate, conservative choice, NOT independently measured against",
        "    /// real SSIS overflow behavior (a single overflowing row would roll back the whole",
        "    /// fast-load batch in the same probe run that measured the rounding rule above, so it",
        "    /// was left untested rather than guessed -- see that same SQL file's header).",
        "    /// </summary>",
        "    public static int NarrowR8ToI4(double value) => checked((int)Math.Round(value));",
        "",
        "    /// <summary>Nullable-source overload -- a NULL source value passes through as NULL,",
        "    /// matching SSIS's own measured behavior for this same pairing.</summary>",
        "    public static int? NarrowR8ToI4(double? value) => value is null ? null : NarrowR8ToI4(value.Value);",
    ];

    private static List<string> NarrowI8ToI4Body() =>
    [
        "    /// <summary>",
        "    /// A plain passthrough column whose source buffer is long (DT_I8) but whose",
        "    /// destination column is int (DT_I4), with no explicit Data Conversion component --",
        "    /// built speculatively 2026-08-30, no real evidenced instance in the tracked",
        "    /// portfolio. Both sides are already integers, so there is no rounding question at",
        "    /// all (unlike NarrowR8ToI4/NarrowNumericToI4/NarrowR4ToI4) -- an out-of-range value",
        "    /// throws OverflowException (checked) rather than silently wrapping, the same",
        "    /// deliberate, conservative, NOT independently measured overflow choice every other",
        "    /// narrowing helper in this file makes.",
        "    /// </summary>",
        "    public static int NarrowI8ToI4(long value) => checked((int)value);",
        "",
        "    /// <summary>Nullable-source overload -- a NULL source value passes through as NULL.</summary>",
        "    public static int? NarrowI8ToI4(long? value) => value is null ? null : NarrowI8ToI4(value.Value);",
    ];

    private static List<string> NarrowNumericToI4Body() =>
    [
        "    /// <summary>",
        "    /// A plain passthrough column whose source buffer is decimal (DT_NUMERIC) but whose",
        "    /// destination column is int (DT_I4), with no explicit Data Conversion component --",
        "    /// built speculatively 2026-08-30 (synthetic-int-numeric-coercion-tables.sql), no real",
        "    /// evidenced instance in the tracked portfolio. Measured via a real dtexec run: rounds",
        "    /// to the NEAREST integer, ties-to-EVEN -- the exact same rule as NarrowR8ToI4",
        "    /// (2.5 -> 2, 3.5 -> 4, 4.5 -> 4, -3.5 -> -4), matching .NET's own default",
        "    /// Math.Round(decimal) midpoint rule exactly. An out-of-range result throws",
        "    /// OverflowException (checked) rather than silently wrapping -- a deliberate,",
        "    /// conservative choice, NOT independently measured against real SSIS overflow",
        "    /// behavior, same reasoning as NarrowR8ToI4's own doc comment.",
        "    /// </summary>",
        "    public static int NarrowNumericToI4(decimal value) => checked((int)Math.Round(value));",
        "",
        "    /// <summary>Nullable-source overload -- a NULL source value passes through as NULL.</summary>",
        "    public static int? NarrowNumericToI4(decimal? value) => value is null ? null : NarrowNumericToI4(value.Value);",
    ];

    private static List<string> NarrowR4ToI4Body() =>
    [
        "    /// <summary>",
        "    /// A plain passthrough column whose source buffer is float, single-precision (DT_R4)",
        "    /// but whose destination column is int (DT_I4), with no explicit Data Conversion",
        "    /// component -- built speculatively 2026-08-30 (synthetic-int-numeric-coercion-tables.sql),",
        "    /// no real evidenced instance in the tracked portfolio. Measured via a real dtexec run:",
        "    /// the exact same round-to-nearest-ties-to-even rule as NarrowR8ToI4 (2.5 -> 2,",
        "    /// 3.5 -> 4, 4.5 -> 4, -3.5 -> -4) -- widened to double before rounding since",
        "    /// Math.Round has no float overload, an exact, lossless widening for every value this",
        "    /// pairing can produce. An out-of-range result throws OverflowException (checked)",
        "    /// rather than silently wrapping -- a deliberate, conservative choice, NOT",
        "    /// independently measured against real SSIS overflow behavior, same reasoning as",
        "    /// NarrowR8ToI4's own doc comment.",
        "    /// </summary>",
        "    public static int NarrowR4ToI4(float value) => checked((int)Math.Round((double)value));",
        "",
        "    /// <summary>Nullable-source overload -- a NULL source value passes through as NULL.</summary>",
        "    public static int? NarrowR4ToI4(float? value) => value is null ? null : NarrowR4ToI4(value.Value);",
    ];

    private static List<string> ParseWstrToI4Body() =>
    [
        "    /// <summary>",
        "    /// A plain passthrough column whose source buffer is string (DT_WSTR) but whose",
        "    /// destination column is int (DT_I4), with no explicit Data Conversion component --",
        "    /// the real shape RBC_Demo_ETL's own Package_Exports/DFT_AdoNetRoundTrip has",
        "    /// (CustomerID: wstr,20 from an ADO NET Source reading a text-typed staging column,",
        "    /// i4 at the destination table). SSIS's own OLE DB Destination accepts this pairing",
        "    /// and performs the conversion itself at insert time; measured empirically against a",
        "    /// real dtexec run (synthetic-string-to-int-coercion-tables.sql): the string is",
        "    /// parsed as a NUMBER first (accepts a leading sign, a decimal point, scientific",
        "    /// notation, and locale-neutral leading/trailing whitespace), then rounded to the",
        "    /// nearest integer, ties-to-EVEN -- the exact same rounding rule as NarrowR8ToI4",
        "    /// above. Unlike NarrowR8ToI4, this pairing's FAILURE mode was also measured, not",
        "    /// left conservative and unverified: non-numeric text, an empty string, a",
        "    /// whitespace-only string, and an out-of-range value all made the real probe FAIL",
        "    /// HARD (\"Invalid character value for cast specification\", DTSER_FAILURE) rather",
        "    /// than null out or drop the row -- this throws (FormatException/OverflowException)",
        "    /// to match that. Only a literal NULL source value passes through as NULL.",
        "    /// </summary>",
        "    public static int? ParseWstrToI4(string? value) =>",
        "        value is null ? null : checked((int)Math.Round(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)));",
    ];

    private static List<string> ToNullableR8Body() =>
    [
        "    /// <summary>",
        "    /// Data Conversion to DT_R8 with both dispositions IgnoreFailure -- same",
        "    /// empirically-measured semantics as ToNullableI4 above (NULL on any failure, never a",
        "    /// dropped row or thrown exception); accepts scientific notation and locale-neutral",
        "    /// leading/trailing whitespace, matching a real measured dtexec run",
        "    /// (synthetic-data-conversion-types-tables.sql). Built speculatively (2026-08-28) --",
        "    /// no real package needs DT_R8 conversion yet, this measurement was taken ahead of",
        "    /// demand.",
        "    /// </summary>",
        "    public static double? ToNullableR8(string? value) =>",
        "        !string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)",
        "            ? result",
        "            : null;",
    ];

    private static List<string> ToNullableBoolBody() =>
    [
        "    /// <summary>",
        "    /// Data Conversion to DT_BOOL with both dispositions IgnoreFailure -- measured",
        "    /// against a real dtexec run (synthetic-data-conversion-types-tables.sql): accepts",
        "    /// \"True\"/\"False\" (case-insensitive) AND the numeric strings \"1\"/\"0\" -- any other",
        "    /// text, an empty string, or NULL all yield NULL, never a dropped row or thrown",
        "    /// exception. Built speculatively (2026-08-28) -- no real package needs DT_BOOL",
        "    /// conversion yet, this measurement was taken ahead of demand.",
        "    /// </summary>",
        "    public static bool? ToNullableBool(string? value)",
        "    {",
        "        if (string.IsNullOrEmpty(value)) return null;",
        "        if (bool.TryParse(value, out var parsed)) return parsed;",
        "        return value switch { \"0\" => false, \"1\" => true, _ => null };",
        "    }",
    ];

    private static List<string> ToNullableI2Body() =>
    [
        "    /// <summary>",
        "    /// Data Conversion to DT_I2 with both dispositions IgnoreFailure -- built speculatively",
        "    /// 2026-08-30 (synthetic-data-conversion-types2-tables.sql), no real package needs DT_I2",
        "    /// conversion yet. Measured against a real dtexec run: same empirically-measured",
        "    /// semantics as ToNullableI4 above -- NULL on any failure (non-numeric/empty/NULL),",
        "    /// never a dropped row or thrown exception; a valid value surrounded by whitespace",
        "    /// still parses (locale-neutral).",
        "    /// </summary>",
        "    public static short? ToNullableI2(string? value) =>",
        "        !string.IsNullOrEmpty(value) && short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)",
        "            ? result",
        "            : null;",
    ];

    private static List<string> ToNullableI8Body() =>
    [
        "    /// <summary>",
        "    /// Data Conversion to DT_I8 with both dispositions IgnoreFailure -- built speculatively",
        "    /// 2026-08-30 (synthetic-data-conversion-types2-tables.sql), no real package needs DT_I8",
        "    /// conversion yet. Measured against a real dtexec run: same empirically-measured",
        "    /// semantics as ToNullableI4 above -- NULL on any failure, never a dropped row or",
        "    /// thrown exception.",
        "    /// </summary>",
        "    public static long? ToNullableI8(string? value) =>",
        "        !string.IsNullOrEmpty(value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)",
        "            ? result",
        "            : null;",
    ];

    private static List<string> ToNullableDateTimeBody() =>
    [
        "    /// <summary>",
        "    /// Data Conversion to DT_DBTIMESTAMP with both dispositions IgnoreFailure -- built",
        "    /// speculatively 2026-08-30 (synthetic-data-conversion-types2-tables.sql), no real",
        "    /// package needs DT_DBTIMESTAMP conversion yet. Measured against a real dtexec run:",
        "    /// same empirically-measured semantics as ToNullableDate above (NULL on any failure),",
        "    /// just DateTime (date + time) rather than DateOnly (date only) -- a genuinely",
        "    /// different .NET type, not just a renamed copy of ToNullableDate.",
        "    /// </summary>",
        "    public static DateTime? ToNullableDateTime(string? value) =>",
        "        !string.IsNullOrEmpty(value) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)",
        "            ? result",
        "            : null;",
    ];

    private static List<string> ToWstrBody() =>
    [
        "    /// <summary>",
        "    /// Data Conversion to DT_WSTR,n with both dispositions IgnoreFailure -- measured",
        "    /// against a real dtexec run (synthetic-data-conversion-types-tables.sql): a",
        "    /// genuinely DIFFERENT failure mode from every other Data Conversion target -- an",
        "    /// overlong source string is TRUNCATED to fit the declared width, never nulled out",
        "    /// (there is no real \"failure\" to ignore here, unlike a bad numeric/date/bool",
        "    /// string). NULL passes through as NULL; an empty string passes through as an empty",
        "    /// string, not NULL. Built speculatively (2026-08-28) -- no real package needs",
        "    /// DT_WSTR conversion yet, this measurement was taken ahead of demand.",
        "    /// </summary>",
        "    public static string? ToWstr(string? value, int maxLength) =>",
        "        value is null || value.Length <= maxLength ? value : value[..maxLength];",
    ];
}
