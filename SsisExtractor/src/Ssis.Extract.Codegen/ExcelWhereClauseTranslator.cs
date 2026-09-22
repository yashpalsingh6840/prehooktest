using System.Text.RegularExpressions;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Translates the WHERE half of an Excel Source's AccessMode=2 (SqlCommand) query into a C#
/// <c>Func&lt;TRow, bool&gt;</c> predicate body, for wrapping <c>Etl.Core.Excel.ExcelRowSource{TRow}</c>
/// in a <c>FilteringRowSource{TRow}</c>. Built gap-audit Phase 3.4, 2026-09-02 -- speculatively,
/// no real package in the tracked portfolio uses Excel Source SqlCommand mode at all (see
/// <c>PackageGenerator.BuildExcelFlowSource</c>'s own doc comment).
///
/// A small, deliberately hand-rolled parser, not ScriptDom -- Jet/ACE's own SQL dialect is not
/// guaranteed T-SQL-compatible, and the grammar accepted here is narrow by design: an AND-chain
/// (no OR, no parentheses, no functions) of <c>&lt;column&gt; &lt;op&gt; &lt;literal&gt;</c>
/// comparisons, exactly the shape the plan's own scope named. Anything else is a named,
/// honest gap, never guessed.
///
/// <b>Column-list narrowing/reordering (e.g. "SELECT ColA, ColB FROM [Sheet$]") is deliberately
/// NOT handled here or anywhere in this tool, and this is a permanent architectural ceiling, not
/// a future roadmap item.</b> Confirmed via a real raw <c>System.Data.OleDb</c> probe against the
/// checked-in workbook (gap-audit Phase 3.4): ACE OLEDB genuinely reorders/narrows its result set
/// to match the SELECT list's own stated order -- <c>SELECT ReviewedBy, CustomerID FROM
/// [Sheet$]</c> returned columns in exactly that order, not the worksheet's physical layout.
/// <c>Etl.Core.Excel.ExcelRowSource{TRow}</c> has no query engine at all (see its own doc
/// comment) -- it reads the raw physical grid via <c>ExcelDataReader</c>, always in the
/// worksheet's true physical column order, with no way to reorder or skip columns. Reproducing a
/// column list would require knowing the worksheet's TRUE physical layout, which the <c>.dtsx</c>
/// alone never records for a narrowed/reordered query (only the resulting, already-narrowed
/// output columns are persisted) -- and this tool never opens the real data file during
/// <c>ssisx generate</c> (every other emitter is purely <c>.dtsx</c>-schema-driven; peeking at a
/// live workbook would be a new, one-off dependency direction found nowhere else in this
/// project). So a naive "read ordinal N of the SELECT list" translation would silently read the
/// WRONG physical column whenever the list reorders or skips one -- worse than a gap, a silent
/// correctness bug. <c>WHERE</c>, by contrast, is safe: it only ever appears here alongside a
/// still-bare <c>SELECT *</c> (this translator is invoked only when the whole column list is
/// exactly <c>*</c>), so nothing about physical ordering changes -- it composes with the already-
/// proven bare-worksheet read via <c>FilteringRowSource{TRow}</c>, a pure post-hoc row filter.
///
/// <b>String comparison is measured case-INSENSITIVE</b> -- the same probe found
/// <c>WHERE DripEligible = 'y'</c> matched the identical 6 rows as <c>= 'Y'</c>. This is Jet/
/// ACE SQL's own "Text" comparison mode (a database-level setting, not per-operator), genuinely
/// different from -- and not to be confused with -- <c>Ssis.Runtime.Expressions</c>' own
/// oracle-verified SSIS EXPRESSION-language semantics (ordinal <c>==</c>/culture-aware
/// <c>&lt;</c>/<c>&gt;</c>), which govern a wholly separate SSIS surface (Derived Column/
/// Conditional Split expressions) with its own independent measurement. Every string operator
/// here (<c>=</c>/<c>&lt;&gt;</c>/<c>&lt;</c>/<c>&gt;</c>/<c>&lt;=</c>/<c>&gt;=</c>) uses
/// <see cref="StringComparison.OrdinalIgnoreCase"/> uniformly -- only <c>=</c> was directly
/// measured to be case-insensitive, but Jet/ACE's Text-comparison mode is a single database
/// property governing every string operator alike, not an operator-specific rule, so extending
/// the one measured fact to the whole operator family (rather than re-deriving each one) matches
/// this project's own established practice (e.g. numeric <c>+</c> dispatch, gate-2 comparison
/// families) once the underlying MECHANISM, not just one instance of it, is understood.
///
/// <c>!=</c> is a real, measured Jet/ACE SQL syntax error ("Syntax error (missing operator)") --
/// only <c>&lt;&gt;</c> is valid. Since this parser only ever reads an author's own already-
/// SSIS-validated SQL text (a package that saved successfully already passed SSDT's own
/// validation), a <c>!=</c> could never legitimately appear here; it is simply not part of the
/// accepted grammar, and un-recognized text degrades to the ordinary "not a simple comparison"
/// gap rather than a special case.
/// </summary>
internal static partial class ExcelWhereClauseTranslator
{
    private static readonly string[] StringClrTypes = ["string"];
    private static readonly string[] NumericClrTypes = ["double", "float", "int", "long", "short", "decimal"];

    [GeneratedRegex(@"^\s*(?<col>\[[^\[\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*(?<op><>|<=|>=|=|<|>)\s*(?<lit>'(?:[^']|'')*'|-?\d+(?:\.\d+)?)\s*$")]
    private static partial Regex ConditionPattern();

    /// <summary>Translates <paramref name="whereText"/> (everything after the WHERE keyword, no
    /// leading/trailing whitespace assumed) into a C# boolean expression referencing
    /// <c>row.{ColumnName}</c>, or a human-readable gap reason naming exactly what could not be
    /// translated. <paramref name="columnClrTypesByName"/> is the Excel Source's own resolved
    /// output columns (case-insensitive by name, matching Jet/ACE SQL's own identifier
    /// convention and the worksheet header's own text origin).</summary>
    internal static (string? Expression, string? Gap) Translate(string whereText, IReadOnlyDictionary<string, string> columnClrTypesByName)
    {
        var rawConditions = Regex.Split(whereText.Trim(), @"\s+AND\s+", RegexOptions.IgnoreCase);
        var translated = new List<string>();

        foreach (var raw in rawConditions)
        {
            var condition = raw.Trim();
            var match = ConditionPattern().Match(condition);
            if (!match.Success)
            {
                return (null, $"has a WHERE clause with a condition this tool cannot translate ('{condition}') -- only an AND-chain of '<column> <op> <literal>' comparisons is supported (no OR, no parentheses, no functions)");
            }

            var columnName = match.Groups["col"].Value.Trim('[', ']');
            if (!columnClrTypesByName.TryGetValue(columnName, out var clrType))
            {
                return (null, $"has a WHERE clause referencing column '{columnName}', which is not one of this worksheet's own resolved output columns");
            }

            // The real worksheet header text -- an identifier-position use (row.{X}) needs the
            // sanitized C# name ExcelRowEmitter actually declared for this same column; the
            // dictionary lookup above and every gap message keep the raw header text.
            var columnIdentifier = PackageGenerator.SanitizeIdentifier(columnName);

            var op = match.Groups["op"].Value;
            var literal = match.Groups["lit"].Value;
            var isStringLiteral = literal.StartsWith('\'');

            if (isStringLiteral)
            {
                if (!StringClrTypes.Contains(clrType))
                {
                    return (null, $"has a WHERE clause comparing column '{columnName}' (CLR type '{clrType}') against a string literal -- type mismatch");
                }

                var value = literal[1..^1].Replace("''", "'");
                var csLiteral = ProgramEmitter.CSharpStringLiteral(value);
                // Fully-qualified System.StringComparison, matching ExpressionTranslator's own
                // convention (never rely on the embedding file having `using System;`, even
                // though ImplicitUsings=enable happens to provide it for every real generated
                // project today).
                translated.Add(op switch
                {
                    "=" => $"string.Equals(row.{columnIdentifier}, {csLiteral}, System.StringComparison.OrdinalIgnoreCase)",
                    "<>" => $"!string.Equals(row.{columnIdentifier}, {csLiteral}, System.StringComparison.OrdinalIgnoreCase)",
                    "<" => $"string.Compare(row.{columnIdentifier}, {csLiteral}, System.StringComparison.OrdinalIgnoreCase) < 0",
                    ">" => $"string.Compare(row.{columnIdentifier}, {csLiteral}, System.StringComparison.OrdinalIgnoreCase) > 0",
                    "<=" => $"string.Compare(row.{columnIdentifier}, {csLiteral}, System.StringComparison.OrdinalIgnoreCase) <= 0",
                    ">=" => $"string.Compare(row.{columnIdentifier}, {csLiteral}, System.StringComparison.OrdinalIgnoreCase) >= 0",
                    _ => throw new InvalidOperationException($"unreachable: ConditionPattern only matches <>,<=,>=,=,<,> but got '{op}'"),
                });
            }
            else
            {
                if (!NumericClrTypes.Contains(clrType))
                {
                    return (null, $"has a WHERE clause comparing column '{columnName}' (CLR type '{clrType}') against a numeric literal -- type mismatch");
                }

                var csOp = op switch { "=" => "==", "<>" => "!=", _ => op };
                translated.Add($"row.{columnIdentifier} {csOp} {literal}");
            }
        }

        return (string.Join(" && ", translated), null);
    }
}
