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
        // Only "yy" is in the oracle corpus (DATEPART("yy", ...) => year). The rest of this
        // map follows T-SQL DATEPART's own codes by analogy and is NOT individually verified
        // against the real SSIS evaluator -- add a corpus case before depending on one.
        return part.ToLowerInvariant() switch
        {
            "yy" or "yyyy" or "year" => SsisValue.OfInt(date.Year),
            "mm" or "m" or "month" => SsisValue.OfInt(date.Month),
            "dd" or "d" or "day" => SsisValue.OfInt(date.Day),
            "hh" or "hour" => SsisValue.OfInt(date.Hour),
            "mi" or "n" or "minute" => SsisValue.OfInt(date.Minute),
            "ss" or "s" or "second" => SsisValue.OfInt(date.Second),
            _ => throw new SsisExpressionError($"{fn}: unrecognised date part '{part}'"),
        };
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
}
