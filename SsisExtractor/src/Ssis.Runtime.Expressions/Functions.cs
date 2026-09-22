namespace Ssis.Runtime.Expressions;

/// <summary>
/// The SSIS expression-language functions covered here -- exactly the ones the ground-truth
/// oracle corpus exercises (docs/gate2-schema.md's function catalog), not the full built-in
/// function list. <c>ISNULL</c>/<c>REPLACENULL</c> are the only two that ever see a null
/// argument; every other function propagates null generically (measured: <c>UPPER(NULL)</c>,
/// <c>LEN(NULL)</c>, <c>SUBSTRING(NULL,...)</c>, <c>TOKENCOUNT(NULL,...)</c> all => NULL) --
/// <see cref="Call"/> enforces that once, centrally, rather than duplicating a null check in
/// every function body.
/// </summary>
public static class Functions
{
    /// <summary>Declared return type per function, used only to type a NULL result when an argument is null (the function body itself never runs in that case).</summary>
    private static readonly Dictionary<string, SsisType> ReturnTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UPPER"] = SsisType.WStr,
        ["LOWER"] = SsisType.WStr,
        ["TRIM"] = SsisType.WStr,
        ["LTRIM"] = SsisType.WStr,
        ["RTRIM"] = SsisType.WStr,
        ["LEN"] = SsisType.I4,
        ["SUBSTRING"] = SsisType.WStr,
        ["LEFT"] = SsisType.WStr,
        ["RIGHT"] = SsisType.WStr,
        ["REPLACE"] = SsisType.WStr,
        ["FINDSTRING"] = SsisType.I4,
        ["TOKEN"] = SsisType.WStr,
        ["TOKENCOUNT"] = SsisType.I4,
        ["DATEPART"] = SsisType.I4,
        ["YEAR"] = SsisType.I4,
        ["MONTH"] = SsisType.I4,
        ["DAY"] = SsisType.I4,
        ["DATEDIFF"] = SsisType.I4,
        ["DATEADD"] = SsisType.DbTimeStamp,
    };

    public static SsisValue Call(string name, List<SsisValue> args)
    {
        if (string.Equals(name, "ISNULL", StringComparison.OrdinalIgnoreCase))
        {
            Arity(name, args, 1);
            return SsisValue.OfBool(args[0].IsNull);
        }
        if (string.Equals(name, "REPLACENULL", StringComparison.OrdinalIgnoreCase))
        {
            Arity(name, args, 2);
            return args[0].IsNull ? args[1] : args[0];
        }
        // GETUTCDATE/GETDATE are non-deterministic by design (plan §5.8) -- callers
        // generating tests (ssisx testgen) must exclude any expression that reaches these
        // from exact-value assertions, the same way the non-determinism manifest already
        // excludes GETUTCDATE()-derived columns from golden-dataset comparison (gate 3).
        if (string.Equals(name, "GETUTCDATE", StringComparison.OrdinalIgnoreCase)) { Arity(name, args, 0); return SsisValue.OfDate(DateTime.UtcNow); }
        if (string.Equals(name, "GETDATE", StringComparison.OrdinalIgnoreCase)) { Arity(name, args, 0); return SsisValue.OfDate(DateTime.Now); }

        if (!ReturnTypes.TryGetValue(name, out var returnType))
            throw new SsisExpressionError($"unknown function '{name}' -- not in the measured oracle corpus (docs/gate2-schema.md); add a corpus case before wiring it up");

        if (args.Any(a => a.IsNull)) return SsisValue.Null(returnType);

        return name.ToUpperInvariant() switch
        {
            "UPPER" => SsisValue.OfString(Str(name, args, 0).ToUpperInvariant()),
            "LOWER" => SsisValue.OfString(Str(name, args, 0).ToLowerInvariant()),
            "TRIM" => SsisValue.OfString(Str(name, args, 0).Trim()),
            "LTRIM" => SsisValue.OfString(Str(name, args, 0).TrimStart()),
            "RTRIM" => SsisValue.OfString(Str(name, args, 0).TrimEnd()),
            "LEN" => SsisValue.OfInt(Str(name, args, 0).Length),
            "SUBSTRING" => Substring(name, args),
            "LEFT" => LeftRight(name, args, fromLeft: true),
            "RIGHT" => LeftRight(name, args, fromLeft: false),
            "REPLACE" => Replace(name, args),
            "FINDSTRING" => FindString(name, args),
            "TOKEN" => Token(name, args, wantCount: false),
            "TOKENCOUNT" => Token(name, args, wantCount: true),
            "DATEPART" => DatePart(name, args),
            "DATEDIFF" => DateDiff(name, args),
            "DATEADD" => DateAdd(name, args),
            "YEAR" => SsisValue.OfInt(Date(name, args, 0).Year),
            "MONTH" => SsisValue.OfInt(Date(name, args, 0).Month),
            "DAY" => SsisValue.OfInt(Date(name, args, 0).Day),
            _ => throw new SsisExpressionError($"function '{name}' is declared but not implemented"),
        };
    }

    private static void Arity(string name, List<SsisValue> args, int expected)
    {
        if (args.Count != expected) throw new SsisExpressionError($"{name} expects {expected} argument(s), got {args.Count}");
    }

    private static string Str(string fn, List<SsisValue> args, int index)
    {
        if (index >= args.Count) throw new SsisExpressionError($"{fn}: missing argument {index}");
        if (!SsisTypes.IsString(args[index].Type)) throw new SsisExpressionError($"{fn}: argument {index} must be a string, got {args[index].Type}");
        return args[index].AsString;
    }

    private static long Int(string fn, List<SsisValue> args, int index)
    {
        if (index >= args.Count) throw new SsisExpressionError($"{fn}: missing argument {index}");
        return args[index].AsInt64;
    }

    private static DateTime Date(string fn, List<SsisValue> args, int index)
    {
        if (index >= args.Count) throw new SsisExpressionError($"{fn}: missing argument {index}");
        return args[index].AsDate;
    }

    private static SsisValue Substring(string fn, List<SsisValue> args)
    {
        Arity(fn, args, 3);
        var s = Str(fn, args, 0);
        var start = Int(fn, args, 1);
        var length = Int(fn, args, 2);
        // Measured: start must be a valid 1-based position -- 0 errors exactly like a
        // negative start (SUBSTRING("ABC",0,2) => ERROR, not ""). length=0 is fine (=> "");
        // length<0 => error. A start/length that's in-range but runs past the end of the
        // string clamps to "" rather than erroring.
        if (start < 1) throw new SsisExpressionError($"{fn}: start must be >= 1, got {start}");
        if (length < 0) throw new SsisExpressionError($"{fn}: length must be >= 0, got {length}");
        var zeroIdx = start - 1;
        if (zeroIdx >= s.Length) return SsisValue.OfString("");
        var available = s.Length - zeroIdx;
        var take = Math.Min(length, available);
        return SsisValue.OfString(s.Substring((int)zeroIdx, (int)take));
    }

    private static SsisValue LeftRight(string fn, List<SsisValue> args, bool fromLeft)
    {
        Arity(fn, args, 2);
        var s = Str(fn, args, 0);
        var n = Int(fn, args, 1);
        // Measured: negative count errors; a count exceeding the string length clamps.
        if (n < 0) throw new SsisExpressionError($"{fn}: count must be >= 0, got {n}");
        var take = (int)Math.Min(n, s.Length);
        return SsisValue.OfString(fromLeft ? s[..take] : s[(s.Length - take)..]);
    }

    private static SsisValue Replace(string fn, List<SsisValue> args)
    {
        Arity(fn, args, 3);
        var s = Str(fn, args, 0);
        var find = Str(fn, args, 1);
        var repl = Str(fn, args, 2);
        // Measured: an empty search string is a no-op (returns the input unchanged), not an
        // infinite-insertion result the way String.Replace("","") would be if attempted.
        if (find.Length == 0) return SsisValue.OfString(s);
        return SsisValue.OfString(s.Replace(find, repl, StringComparison.Ordinal));
    }

    private static SsisValue FindString(string fn, List<SsisValue> args)
    {
        Arity(fn, args, 3);
        var s = Str(fn, args, 0);
        var search = Str(fn, args, 1);
        var occurrence = Int(fn, args, 2);
        if (occurrence < 1) throw new SsisExpressionError($"{fn}: occurrence must be >= 1, got {occurrence}");
        // Measured: an empty search string never matches, unconditionally (0), regardless of
        // occurrence -- a deliberate SSIS quirk, not "matches everywhere".
        if (search.Length == 0) return SsisValue.OfInt(0);

        var searchFrom = 0;
        var idx = -1;
        for (var count = 0; count < occurrence; count++)
        {
            idx = s.IndexOf(search, searchFrom, StringComparison.Ordinal);
            if (idx < 0) return SsisValue.OfInt(0);
            searchFrom = idx + 1; // overlapping matches allowed -- matches FINDSTRING("abcabc","bc",2) => 5
        }
        return SsisValue.OfInt(idx + 1); // 1-based
    }

    /// <summary>
    /// TOKEN/TOKENCOUNT: <paramref name="delims"/> is a SET of delimiter characters (like C's
    /// <c>strtok</c>), not a substring -- <c>TOKEN("a;b,c",",;",2) =&gt; "b"</c> proves this.
    /// Consecutive delimiters and leading/trailing delimiters are skipped (empty tokens are
    /// never produced) EXCEPT an empty input string, which is measured as exactly one token
    /// (itself, "") rather than zero -- a genuine SSIS-specific special case, not a bug.
    /// </summary>
    private static SsisValue Token(string fn, List<SsisValue> args, bool wantCount)
    {
        Arity(fn, args, wantCount ? 2 : 3);
        var s = Str(fn, args, 0);
        var delims = Str(fn, args, 1);
        if (delims.Length == 0) throw new SsisExpressionError($"{fn}: delimiter set must not be empty");

        var tokens = s.Length == 0
            ? [""]
            : s.Split(delims.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

        if (wantCount) return SsisValue.OfInt(tokens.Length);

        var index = Int(fn, args, 2);
        // Out-of-range index (either direction) clamps to "" -- measured only for
        // beyond-the-end (TOKEN("a,b,c",",",9) => ""); below-1 is assumed symmetric, not
        // itself measured.
        if (index < 1 || index > tokens.Length) return SsisValue.OfString("");
        return SsisValue.OfString(tokens[index - 1]);
    }

    private static SsisValue DatePart(string fn, List<SsisValue> args)
    {
        Arity(fn, args, 2);
        var part = Str(fn, args, 0);
        var date = Date(fn, args, 1);
        // Only "yy" and "Millisecond" are in the oracle corpus (DATEPART("yy", ...) => year;
        // DATEPART("Millisecond", ...) measured 2026-09-17, Phase 6 of the
        // unsupported-component-types plan). The rest of this map follows T-SQL DATEPART's own
        // codes by analogy and is NOT individually verified against the real SSIS evaluator --
        // add a corpus case before depending on one.
        return part.ToLowerInvariant() switch
        {
            "yy" or "yyyy" or "year" => SsisValue.OfInt(date.Year),
            "mm" or "m" or "month" => SsisValue.OfInt(date.Month),
            "dd" or "d" or "day" => SsisValue.OfInt(date.Day),
            "hh" or "hour" => SsisValue.OfInt(date.Hour),
            "mi" or "n" or "minute" => SsisValue.OfInt(date.Minute),
            "ss" or "s" or "second" => SsisValue.OfInt(date.Second),
            "millisecond" or "ms" => SsisValue.OfInt(QuantizedMillisecond(date)),
            _ => throw new SsisExpressionError($"{fn}: unrecognised date part '{part}'"),
        };
    }

    /// <summary>
    /// DATEPART("Millisecond", date)'s own internal arithmetic engine does NOT read the exact
    /// stored millisecond value -- measured directly against the real SSIS 22 evaluator
    /// 2026-09-17 (sql-server-samples' DailyETLMain.dtsx, "Trim Any Milliseconds"): it quantizes
    /// to the nearest 1/300 of a second (~3.333ms per tick) before reporting the millisecond
    /// count. This is the well-documented legacy "OLE Automation Date" (VT_DATE) time
    /// resolution limit -- its fractional-day component is only reliable to 1/300s, a holdover
    /// from the original VB timer tick rate, not a guess. Measured: DATEPART("Millisecond",
    /// .456) reports 457 (456ms quantizes to tick round(456*0.3)=137, which is 137*10/3 =
    /// 456.667ms, rounding to 457); DATEPART("Millisecond", .999) reports 0 (999ms quantizes to
    /// tick round(999*0.3)=300, i.e. exactly 1000ms, which rolls over to the NEXT second with a
    /// 0ms remainder). A plain (DT_WSTR,n) cast of the same timestamp (no DATEPART/DATEADD
    /// involved) shows the exact, unquantized value -- this quantization is specific to
    /// DatePart/DateAdd's own arithmetic, not how a DT_DBTIMESTAMP is stored or displayed.
    /// </summary>
    private static int QuantizedMillisecond(DateTime date)
    {
        var ticks = Math.Round(date.Millisecond * 0.3, MidpointRounding.ToEven);
        var quantizedMs = (int)Math.Round(ticks * (10.0 / 3.0), MidpointRounding.ToEven);
        return quantizedMs % 1000;
    }

    /// <summary>
    /// DATEDIFF(datepart, startDate, endDate) -- only "dd" is measured against the real
    /// evaluator (2026-08-27, oracle corpus rows added specifically for RBC_Demo_ETL's own
    /// Package_Transforms.dtsx, DER_Enrich.TenureDays -- the only real evidenced call). Counts
    /// calendar-DAY-BOUNDARY crossings, exactly like T-SQL's own DATEDIFF("dd",...) -- NOT
    /// elapsed 24-hour periods: measured 23:00 -&gt; next day 01:00 (only 2 hours elapsed) =&gt; 1,
    /// not 0. Sign convention is endDate - startDate (measured: swapping the two arguments
    /// negates the result). DT_DBDATE and DT_DBTIMESTAMP mix freely with no explicit cast
    /// needed (measured directly, matching TenureDays' own SignupDate_dt/GETDATE() shapes).
    /// Any OTHER datepart literal is a hard error, not a guessed T-SQL-by-analogy value --
    /// unlike DatePart above, no other part has been measured here.
    /// </summary>
    private static SsisValue DateDiff(string fn, List<SsisValue> args)
    {
        Arity(fn, args, 3);
        var part = Str(fn, args, 0);
        if (!string.Equals(part, "dd", StringComparison.OrdinalIgnoreCase))
            throw new SsisExpressionError($"{fn}: only the \"dd\" date part is measured against the real evaluator -- add a corpus case before supporting '{part}'");

        var start = Date(fn, args, 1);
        var end = Date(fn, args, 2);
        return SsisValue.OfInt((end.Date - start.Date).Days);
    }

    /// <summary>
    /// DATEADD(datepart, number, date) -- measured against the real evaluator 2026-09-15, Phase
    /// 2 of the unsupported-component-types plan (Microsoft.ExpressionTask, whose real evidenced
    /// call is DATEADD("Minute", -5, GETUTCDATE())). Every measured datepart maps exactly onto
    /// .NET's own DateTime.AddX methods, INCLUDING their day/month-clamping edge cases -- Jan 31
    /// + 1 month lands on Feb 29 (2020, a leap year) and Feb 29 + 1 year lands on Feb 28 (2021,
    /// not a leap year), both confirmed to match .NET's AddMonths/AddYears exactly, not
    /// guessed by analogy. The datepart literal is matched case-insensitively against the FULL
    /// WORD ("Minute"/"Day"/"Hour"/"Month"/"Year"/"Second") plus the one abbreviation ("mi")
    /// individually measured -- unlike DatePart above, no other abbreviation (e.g. "dd", "hh",
    /// "yy") has been measured for DATEADD specifically, so none is guessed here even though
    /// DatePart's own table already has them for a different function.
    ///
    /// <b>Always returns DT_DBTIMESTAMP, never DT_DBDATE</b> -- measured directly: DATEADD over
    /// a DT_DBDATE input still produces a full timestamp (confirmed via a (DT_WSTR,n) cast
    /// showing "00:00:00" even when the input had no time component). SsisValue.OfDate's own
    /// default type parameter already matches this, so no explicit type override is needed here.
    /// </summary>
    private static SsisValue DateAdd(string fn, List<SsisValue> args)
    {
        Arity(fn, args, 3);
        var part = Str(fn, args, 0);
        var number = (int)Int(fn, args, 1);
        var date = Date(fn, args, 2);

        if (string.Equals(part, "MILLISECOND", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, "MS", StringComparison.OrdinalIgnoreCase))
            return SsisValue.OfDate(AddQuantizedMilliseconds(date, number));

        return SsisValue.OfDate(part.ToUpperInvariant() switch
        {
            "MINUTE" or "MI" => date.AddMinutes(number),
            "DAY" => date.AddDays(number),
            "HOUR" => date.AddHours(number),
            "MONTH" => date.AddMonths(number),
            "YEAR" => date.AddYears(number),
            "SECOND" => date.AddSeconds(number),
            _ => throw new SsisExpressionError(
                $"{fn}: date part '{part}' is not measured against the real evaluator -- only " +
                "\"Minute\"/\"mi\", \"Day\", \"Hour\", \"Month\", \"Year\", \"Second\", \"Millisecond\" are; add a corpus case before supporting it"),
        });
    }

    /// <summary>
    /// DATEADD("Millisecond", number, date) -- measured against the real SSIS 22 evaluator
    /// 2026-09-17 (see <see cref="QuantizedMillisecond"/>'s own doc comment for the shared
    /// 1/300-second-tick discovery). DATEADD's own arithmetic operates entirely in TICKS (1 tick
    /// = 1/300 second): both the base date's own millisecond remainder AND the delta being added
    /// are independently quantized to the nearest tick BEFORE being summed, not added as plain
    /// milliseconds and quantized afterward -- measured directly: DATEADD("Millisecond", 1,
    /// .456) reports the SAME result as DATEADD("Millisecond", 0, .456) (.457), because a 1ms
    /// delta quantizes to round(1*0.3)=0 ticks, i.e. no change at all. This is exactly why the
    /// real motivating idiom (subtract a value's own reported millisecond count from itself)
    /// still lands exactly on the second boundary regardless of the quantization: DATEPART
    /// reports the base's own quantized tick count as milliseconds, and re-quantizing that exact
    /// count as the delta always cancels the base's own ticks precisely.
    /// </summary>
    private static DateTime AddQuantizedMilliseconds(DateTime date, int number)
    {
        var baseTicks = Math.Round(date.Millisecond * 0.3, MidpointRounding.ToEven);
        var deltaTicks = Math.Round(number * 0.3, MidpointRounding.ToEven);
        var totalMs = (baseTicks + deltaTicks) * (10.0 / 3.0);

        var wholeSeconds = (int)Math.Floor(totalMs / 1000.0);
        var remainderMs = (int)Math.Round(totalMs - wholeSeconds * 1000.0, MidpointRounding.ToEven);
        if (remainderMs >= 1000) { wholeSeconds++; remainderMs -= 1000; }
        else if (remainderMs < 0) { wholeSeconds--; remainderMs += 1000; }

        return date.AddMilliseconds(-date.Millisecond).AddSeconds(wholeSeconds).AddMilliseconds(remainderMs);
    }
}
