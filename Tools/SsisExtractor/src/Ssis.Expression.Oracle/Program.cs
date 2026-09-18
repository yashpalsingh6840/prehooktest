using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.SqlServer.Dts.Runtime;

namespace Ssis.Expression.Oracle;

/// <summary>
/// Ground-truth oracle for <c>Ssis.Runtime.Expressions</c> (Gate 2, Migration-Validation-
/// Plan.md §4): evaluates a fixed corpus of SSIS expressions with the REAL SSIS expression
/// evaluator (a package Variable with <c>EvaluateAsExpression=true</c> evaluates on read of
/// <c>.Value</c>) and writes the results to a TSV file. That file is checked into
/// <c>tests/Ssis.Runtime.Expressions.Tests/Fixtures/ssis-expression-oracle.tsv</c> and is what
/// the semantics library's own test suite is verified against -- so the library encodes
/// measured behaviour, not remembered documentation (same "ask the runtime" discipline as
/// CLAUDE.md trap 12).
///
/// <b>Reading <c>.Value</c> silently swallows an evaluation failure</b> and hands back the
/// variable's previous value instead of throwing -- so a naive probe can't tell "evaluated to
/// NULL", "evaluated to empty string", and "failed" apart. Fixed two ways: the probe variable
/// is seeded with a sentinel no real expression could produce, and every expression is
/// evaluated through an <c>ISNULL(x) ? "&lt;&lt;NULL&gt;&gt;" : "&lt;&lt;VAL&gt;&gt;" + (DT_WSTR,400)(x)</c>
/// wrapper so SSIS itself reports nullness. If the wrapper itself fails to evaluate (e.g. the
/// result doesn't cast to DT_WSTR, or the expression is a syntax/runtime error), that's
/// reported as ERROR.
///
/// <b>A second, real bug found and fixed the hard way:</b> an earlier version of this probe
/// created ONE <c>Package</c> and repeatedly called <c>Variables.Add("probe",...)</c> /
/// <c>Remove(v.ID)</c> for every case in one process. That produced an internally
/// CONTRADICTORY reading -- both <c>"a" &lt; "A"</c> and <c>"A" &lt; "a"</c> came back True in
/// the same run, which cannot both be correct under any consistent ordering. Root-caused by
/// re-running the two expressions in total isolation (fresh <c>Application</c>+<c>Package</c>+
/// uniquely-named variable each) and getting a single consistent answer both times -- so the
/// shared Package/reused variable name was leaking stale evaluator state across iterations,
/// not a real SSIS behaviour. Fixed by giving every single case its own <c>Application</c>,
/// <c>Package</c>, and uniquely-numbered variable name, with nothing shared across the corpus.
/// This is slower (a fresh Application per case) but the whole point of an oracle is that its
/// answer can be trusted; a faster probe that might be contaminated is worse than a slow one.
///
/// Regenerate with (needs Windows + the SSIS 17 GAC assemblies CLAUDE.md documents):
///   dotnet run --project src/Ssis.Expression.Oracle -c Release -- &gt; corpus.tsv
/// then diff against the checked-in fixture before overwriting it -- a changed row means
/// either this corpus grew, or (worth investigating) SSIS's own evaluator behaviour differs
/// on this machine/version from what was originally measured.
/// </summary>
internal static class Program
{
    private const string Unset = "<<UNSET>>";
    private static int _counter;

    private static int Main()
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

        stdout.WriteLine("# expression\toutcome\tvalue");
        foreach (var expr in Cases)
        {
            var (outcome, value) = Classify(expr);
            stdout.WriteLine($"{Escape(expr)}\t{outcome}\t{Escape(value ?? "")}");
        }

        stdout.Flush();
        return 0;
    }

    private static (string Outcome, string? Value) Classify(string expr)
    {
        var wrapped = "ISNULL(" + expr + ") ? \"<<NULL>>\" : \"<<VAL>>\" + (DT_WSTR,400)(" + expr + ")";
        var wrappedResult = EvalIsolated(wrapped);

        if (wrappedResult == "<<NULL>>") return ("NULL", null);
        if (wrappedResult != null && wrappedResult.StartsWith("<<VAL>>", StringComparison.Ordinal))
            return ("VALUE", wrappedResult.Substring("<<VAL>>".Length));

        return ("ERROR", null);
    }

    /// <summary>
    /// Evaluates <paramref name="expr"/> in a brand-new Application+Package+uniquely-named
    /// variable, shared with nothing else -- see the type doc comment for why isolation is
    /// non-negotiable here. Returns null when SSIS produced no value (evaluation failed).
    /// </summary>
    private static string? EvalIsolated(string expr)
    {
        var app = new Application();
        var pkg = new Package();
        var varName = "probe_" + _counter++;

        var v = pkg.Variables.Add(varName, false, "User", Unset);
        try
        {
            v.EvaluateAsExpression = true;
            v.Expression = expr;
            var val = v.Value;
            if (val == null) return null;
            var s = val as string ?? (val is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : val.ToString());
            return s == Unset ? null : s;
        }
        catch
        {
            return null;
        }
        finally
        {
            GC.KeepAlive(app);
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

    /// <summary>
    /// The corpus. Grouped by the semantics area it pins, in the order those areas were
    /// investigated -- kept rather than alphabetized so a future addition can see which
    /// question each block was asked to answer.
    /// </summary>
    private static readonly string[] Cases =
    [
        // --- NULL propagation: string concat ---
        "\"a\" + NULL(DT_WSTR,10)",
        "NULL(DT_WSTR,10) + \"a\"",
        "NULL(DT_WSTR,10) + NULL(DT_WSTR,10)",
        "\"a\" + \"\"",

        // --- NULL propagation: functions (NULL in, NULL out -- except ISNULL/REPLACENULL, which exist specifically to break this) ---
        "UPPER(NULL(DT_WSTR,10))",
        "LOWER(NULL(DT_WSTR,10))",
        "LEN(NULL(DT_WSTR,10))",
        "TRIM(NULL(DT_WSTR,10))",
        "SUBSTRING(NULL(DT_WSTR,10),1,2)",
        "ISNULL(NULL(DT_WSTR,10))",
        "ISNULL(\"\")",
        "REPLACENULL(NULL(DT_WSTR,10),\"x\")",
        "REPLACENULL(\"a\",\"x\")",
        "REPLACENULL(NULL(DT_WSTR,10),NULL(DT_WSTR,10))",
        "TOKENCOUNT(NULL(DT_WSTR,10),\",\")",

        // --- NULL propagation: arithmetic / comparison / logical ---
        "1 + NULL(DT_I4)",
        "2 * NULL(DT_I4)",
        "NULL(DT_I4) == 1",
        "NULL(DT_WSTR,10) == \"a\"",
        "NULL(DT_WSTR,10) == NULL(DT_WSTR,10)",
        "SUBSTRING(\"ABC\",1,NULL(DT_I4))",
        "TRUE ? \"a\" : \"b\"",
        "NULL(DT_BOOL) ? \"a\" : \"b\"",
        "\"a\" == \"a\" && NULL(DT_BOOL)",
        "\"a\" == \"b\" && NULL(DT_BOOL)",
        "TRUE || NULL(DT_BOOL)",
        "!NULL(DT_BOOL)",

        // --- SUBSTRING: 1-based, clamps out-of-range to "", but a length/start that isn't
        // a valid non-negative integer (0-length start clamp is fine; negative isn't) errors ---
        "SUBSTRING(\"ABCDE\",1,3)",
        "SUBSTRING(\"AB\",1,3)",
        "SUBSTRING(\"ABC\",0,2)",
        "SUBSTRING(\"ABC\",5,2)",
        "SUBSTRING(\"ABC\",4,1)",
        "SUBSTRING(\"ABC\",1,0)",
        "SUBSTRING(\"ABC\",2,-1)",
        "SUBSTRING(\"ABC\",-1,2)",
        "SUBSTRING(\"ABC\",1,99)",
        "SUBSTRING(\"\",1,1)",
        "SUBSTRING(\"ABC\",\"1\",2)",

        // --- LEFT/RIGHT: clamp when count exceeds length; negative count errors ---
        "LEFT(\"ABC\",5)",
        "RIGHT(\"ABC\",5)",
        "LEFT(\"ABC\",0)",
        "LEFT(\"ABC\",-1)",
        "RIGHT(\"ABC\",-1)",

        // --- casts: DT_WSTR truncation is a hard ERROR (never silent truncation), and
        // DT_I4 rounds half-to-even (banker's rounding) on the fractional part, not
        // "round half away from zero" or a plain truncating cast ---
        "(DT_WSTR,3)\"ABCDE\"",
        "(DT_WSTR,4)\"ABCDE\"",
        "(DT_WSTR,5)\"ABCDE\"",
        "(DT_WSTR,3)\"\"",
        "(DT_WSTR,10)123",
        "(DT_WSTR,10)NULL(DT_I4)",
        "(DT_WSTR,10)TRUE",
        "(DT_I4)\"12\"",
        "(DT_I4)\" 12 \"",
        "(DT_I4)\"+12\"",
        "(DT_I4)\"12abc\"",
        "(DT_I4)\"\"",
        "(DT_I4)0.5",
        "(DT_I4)1.5",
        "(DT_I4)2.5",
        "(DT_I4)-0.5",
        "(DT_I4)-1.5",
        "(DT_I4)-2.5",
        "(DT_I4)12.7",
        "(DT_I4)-12.7",
        "(DT_I4)12.5",
        "(DT_I4)13.5",
        "(DT_I4)TRUE",
        "(DT_STR,3,1252)\"ABCDE\"",
        "(DT_BOOL)\"1\"",
        "(DT_BOOL)\"true\"",
        "(DT_BOOL)\"0\"",
        "(DT_BOOL)\"2\"",
        "(DT_NUMERIC,10,2)\"1.005\"",
        "(DT_R8)\"1e3\"",

        // --- string equality is ORDINAL and case-sensitive; ordering (<,>,<=,>=) is
        // CULTURE-AWARE, not ordinal -- lowercase sorts before uppercase for the SAME
        // letter (a real divergence from a naive C# string.CompareOrdinal rewrite) ---
        "\"a\" == \"A\"",
        "\"a\" != \"A\"",
        "\"a\" < \"A\"",
        "\"a\" > \"A\"",
        "\"a\" <= \"A\"",
        "\"a\" >= \"A\"",
        "\"a\" < \"B\"",
        "\"B\" > \"a\"",
        "\"b\" > \"A\"",
        "\"Z\" < \"a\"",
        "\"apple\" < \"Banana\"",
        "\"_\" < \"a\"",
        "\"1\" < \"a\"",
        "\"a \" == \"a\"",
        "\"a\" + \"b\" == \"ab\"",

        // --- case folding / culture-sensitive functions ---
        "UPPER(\"ABC\")",
        "UPPER(\"straße\")",
        "UPPER(\"i\")",
        "LOWER(\"İ\")",

        // --- TRIM/LTRIM/RTRIM ---
        "TRIM(\"  a  \")",
        "LTRIM(\"  a  \") + \"|\"",
        "RTRIM(\"  a  \") + \"|\"",
        "LEN(\"\")",
        "LEN(\"  a  \")",

        // --- TOKEN / TOKENCOUNT: 1-based; a delimiter that never matches yields the whole
        // string as token 1; consecutive delimiters yield empty tokens; out-of-range index
        // yields ""; an empty delimiter set errors ---
        "TOKEN(\"a,b,c\",\",\",2)",
        "TOKEN(\"a,b,c\",\",\",9)",
        "TOKENCOUNT(\"a,,c\",\",\")",
        "TOKENCOUNT(\",a,\",\",\")",
        "TOKENCOUNT(\"\",\",\")",
        "TOKEN(\"a,,c\",\",\",2)",
        "TOKEN(\",a\",\",\",1)",
        "TOKENCOUNT(\"a;b,c\",\",;\")",
        "TOKEN(\"a;b,c\",\",;\",2)",
        "TOKEN(\"\",\",\",1)",
        "TOKEN(\"a,b\",\"\",1)",
        "TOKENCOUNT(\"a,b\",\"\")",

        // --- REPLACE / FINDSTRING ---
        "REPLACE(\"aXa\",\"X\",\"\")",
        "REPLACE(\"abc\",\"\",\"X\")",
        "FINDSTRING(\"abcabc\",\"b\",2)",
        "FINDSTRING(\"abcabc\",\"bc\",2)",
        "FINDSTRING(\"abc\",\"z\",1)",
        "FINDSTRING(\"abc\",\"\",1)",
        "FINDSTRING(\"abc\",\"B\",1)",
        "FINDSTRING(\"abc\",\"b\",0)",

        // --- dates ---
        "DATEPART(\"yy\",(DT_DBTIMESTAMP)\"2020-02-29 13:45:00\")",
        "YEAR((DT_DBTIMESTAMP)\"2020-02-29\")",
        "(DT_WSTR,50)(DT_DBTIMESTAMP)\"2020-02-29 13:45:00.123\"",
        "(DT_WSTR,20)(DT_DBDATE)\"2020-02-29\"",

        // --- DATEDIFF("dd", ...): the only datepart evidenced (RBC_Demo_ETL's own
        // Package_Transforms.dtsx, DER_Enrich.TenureDays). Questions this block resolves:
        // sign convention (date2-date1 like T-SQL, or the reverse), whether it counts elapsed
        // 24-hour periods or calendar-day-boundary crossings (T-SQL's own DATEDIFF("dd",...)
        // counts boundaries -- 23:00 to 01:00 next day is 1, not 0, despite only 2 hours
        // elapsed), NULL propagation, and whether DT_DBDATE/DT_DBTIMESTAMP can mix without an
        // explicit cast (SignupDate_dt is DT_DBDATE, GETDATE() returns DT_DBTIMESTAMP).
        "DATEDIFF(\"dd\",(DT_DBDATE)\"2020-01-01\",(DT_DBDATE)\"2020-01-05\")",
        "DATEDIFF(\"dd\",(DT_DBDATE)\"2020-01-05\",(DT_DBDATE)\"2020-01-01\")",
        "DATEDIFF(\"dd\",(DT_DBDATE)\"2020-01-01\",(DT_DBDATE)\"2020-01-01\")",
        "DATEDIFF(\"dd\",NULL(DT_DBDATE),(DT_DBDATE)\"2020-01-01\")",
        "DATEDIFF(\"dd\",(DT_DBDATE)\"2020-01-01\",NULL(DT_DBDATE))",
        "DATEDIFF(\"dd\",(DT_DBTIMESTAMP)\"2020-01-01 23:00:00\",(DT_DBTIMESTAMP)\"2020-01-02 01:00:00\")",
        "DATEDIFF(\"dd\",(DT_DBDATE)\"2020-01-01\",(DT_DBTIMESTAMP)\"2020-01-01 23:59:00\")",
        "DATEDIFF(\"dd\",(DT_DBDATE)\"2020-01-01\",(DT_DBTIMESTAMP)\"2020-01-02 00:00:01\")",

        // --- DATEADD(datepart, number, date): the real evidenced call, Phase 2 of the
        // unsupported-component-types plan (Microsoft.ExpressionTask), is
        // DATEADD("Minute", -5, GETUTCDATE()). Questions this block resolves: whether the
        // datepart literal matches T-SQL's own full-word spelling ("Minute", not "mi"/"n"),
        // sign convention (a negative number subtracts), NULL propagation, whether DT_DBDATE
        // and DT_DBTIMESTAMP mix freely as the base date the same way DATEDIFF's own two date
        // arguments do, and the returned type/precision (does adding minutes to a DT_DBDATE
        // promote it to a timestamp).
        "DATEADD(\"Minute\",-5,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00\")",
        "DATEADD(\"Minute\",5,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00\")",
        "DATEADD(\"Day\",1,(DT_DBDATE)\"2020-01-01\")",
        "DATEADD(\"Day\",-1,(DT_DBDATE)\"2020-01-01\")",
        "DATEADD(\"Hour\",25,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00\")",
        "DATEADD(\"Month\",1,(DT_DBDATE)\"2020-01-31\")",
        "DATEADD(\"Year\",1,(DT_DBDATE)\"2020-02-29\")",
        "DATEADD(\"Second\",90,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00\")",
        "DATEADD(\"Minute\",0,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00\")",
        "DATEADD(\"Minute\",-5,NULL(DT_DBTIMESTAMP))",
        "(DT_WSTR,30)DATEADD(\"Minute\",-5,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00\")",
        "(DT_WSTR,30)DATEADD(\"Day\",1,(DT_DBDATE)\"2020-01-01\")",
        "DATEADD(\"minute\",-5,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00\")",
        "DATEADD(\"mi\",-5,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00\")",
        "(DT_WSTR,30)(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.000\"",
        "(DT_WSTR,30)(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.500\"",

        // --- arithmetic typing: integer division truncates, mixed int/float promotes,
        // there is no implicit string<->number coercion in either direction ---
        "5/2",
        "5.0/2",
        "(DT_WSTR,20)(5/2)",
        "(DT_WSTR,20)(5.0/2)",
        "(DT_WSTR,20)(1 + 2.5)",
        "1 == 1.0",
        "1 < 2.5",
        "5 % 3",
        "5 % 0",
        "-5 % 3",
        "1 + \"a\"",
        "\"1\" + 1",
        "LEN(123)",
        "TRUE + 1",
        "-\"a\"",

        // --- DATEADD("Millisecond", ...) / DATEPART("Millisecond", ...): Phase 6 of the
        // unsupported-component-types plan. The real evidenced call is sql-server-samples'
        // DailyETLMain.dtsx, Microsoft.ExpressionTask "Trim Any Milliseconds":
        //   @[User::TargetETLCutoffTime] = DATEADD("Millisecond",
        //       0 - DATEPART("Millisecond", @[User::TargetETLCutoffTime]),
        //       @[User::TargetETLCutoffTime])
        // Questions this block resolves: whether "Millisecond" is accepted as a datepart
        // literal at all; sign convention for DATEADD; NULL propagation for both functions;
        // and whether a plain integer subtraction (0 - DATEPART(...)) needs anything beyond
        // ordinary arithmetic.
        //
        // GENUINELY SURPRISING FINDING, not guessed, confirmed by these rows: DATEADD/DATEPART's
        // own internal date arithmetic quantizes sub-second time to the nearest 1/300 of a
        // second (~3.333ms per tick) -- the well-documented legacy "OLE Automation Date" time
        // resolution limit (VT_DATE's fractional-day component is only reliable to 1/300s, a
        // holdover from the original VB timer tick rate), NOT true 1ms precision. Measured:
        // DATEPART("Millisecond", .456) reports 457 (456ms quantizes to tick 137 -> 456.667ms,
        // rounds to 457), and DATEPART("Millisecond", .999) reports 0 (999ms quantizes to tick
        // 300 = exactly 1000ms, which rolls over to the NEXT second with a 0ms remainder) -- a
        // plain (DT_WSTR,n) CAST of the same .456 timestamp (no DATEADD/DATEPART involved) shows
        // .456 exactly, so the quantization is specific to these two functions' own arithmetic
        // engine, not how the value is stored/displayed. Crucially, DATEPART reads the SAME
        // quantized representation DATEADD's own arithmetic operates on, so the real task's own
        // idiom -- subtract a value's own reported millisecond count from itself -- still zeroes
        // the fraction EXACTLY regardless of the quantization (confirmed on both .789 and an
        // already-whole-second .000 input below).
        "DATEPART(\"Millisecond\",(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.123\")",
        "DATEPART(\"Millisecond\",(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.000\")",
        "DATEPART(\"Millisecond\",(DT_DBDATE)\"2020-01-01\")",
        "DATEPART(\"Millisecond\",NULL(DT_DBTIMESTAMP))",
        "DATEPART(\"Millisecond\",(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.456\")",
        "DATEPART(\"Millisecond\",(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.999\")",
        "DATEADD(\"Millisecond\",500,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.000\")",
        "DATEADD(\"Millisecond\",-123,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.123\")",
        "DATEADD(\"Millisecond\",1500,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.000\")",
        "DATEADD(\"Millisecond\",0,(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.456\")",
        "DATEADD(\"Millisecond\",-5,NULL(DT_DBTIMESTAMP))",
        "DATEADD(\"Millisecond\",0 - DATEPART(\"Millisecond\",(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.789\"),(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.789\")",
        "DATEADD(\"Millisecond\",0 - DATEPART(\"Millisecond\",(DT_DBTIMESTAMP)\"2020-06-15 12:30:45.000\"),(DT_DBTIMESTAMP)\"2020-06-15 12:30:45.000\")",
        "0 - 5",
        "5 - 0",
        "0 - DATEPART(\"Millisecond\",(DT_DBTIMESTAMP)\"2020-01-01 00:00:00.007\")",
    ];
}
