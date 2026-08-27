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
        if (functionsUsed.Contains("Str")) bodies.Add(StrBody());

        var lines = new List<string>();
        if (functionsUsed.Contains("Str"))
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

    private static List<string> StrBody() =>
    [
        "    /// <summary>(DT_WSTR,n) cast of an integer -- invariant, no thousands separator.</summary>",
        "    public static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);",
    ];
}
