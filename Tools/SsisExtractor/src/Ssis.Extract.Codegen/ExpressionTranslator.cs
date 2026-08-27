using System.Text;
using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen;

/// <summary>Result of translating one SSIS expression AST into C# source text.</summary>
public abstract record TranslatedExpression;

public sealed record TranslatedOk(string CSharpExpression) : TranslatedExpression;

/// <summary>An expression shape outside what's evidenced across the PoC's own packages
/// (plan §2.1's own rule: degrade the one column, never guess a plausible-looking
/// translation). <see cref="Reason"/> is what a human reads to decide how to fill the gap.</summary>
public sealed record NotTranslatable(string Reason) : TranslatedExpression;

/// <summary>
/// Translates one <c>ExprNode</c> (already parsed by <c>SsisExpression.Parse</c>) into a C#
/// expression string. Covers exactly the shapes evidenced across both real PoC packages'
/// Derived Column transforms -- string concatenation, UPPER, SUBSTRING, GETUTCDATE, an outer
/// (DT_WSTR/DT_STR,n) cast (the column's own declared output width), and a nested
/// (DT_WSTR/DT_STR,n) cast directly over an integral reference (an int-to-string conversion
/// with no width check of its own). Anything else returns <see cref="NotTranslatable"/>.
///
/// The outer cast maps to <c>Etl.Core.Ssis.WidthGuard.Wstr(...)</c>, NOT a generated
/// <c>SsisFn.Wstr</c> the way the original generate plan's own worked example sketched --
/// WidthGuard/SsisWidthAttribute/SsisTruncationException are generic width-enforcement
/// plumbing and stayed in the shared Etl.Core skeleton (D:\PoC\SSIS_Rewrite), unlike the
/// per-function SSIS semantics (Upper/Substring/Str) that <see cref="SsisFnEmitter"/> emits
/// per package. See CLAUDE.md's "ssis-rewrite-skeleton-decisions" note for why that split
/// exists; this translator targets that already-built skeleton, not the plan's earlier sketch.
/// </summary>
public static class ExpressionTranslator
{
    /// <summary>
    /// Entry point: translates one output column's whole expression. <paramref name="references"/>
    /// resolves every bare identifier the expression can reference (e.g. "FirstName") to the
    /// C# expression that reads it (e.g. "row.FirstName") plus its SsisType -- the caller
    /// already knows this from lineage (see <see cref="TransformEmitter"/>), so this type
    /// doesn't re-derive it.
    /// </summary>
    public static TranslatedExpression TranslateColumn(
        ExprNode root,
        string entityName,
        string columnName,
        IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references)
    {
        if (root is FunctionCall { Name: "GETUTCDATE" or "GETDATE", Args.Count: 0 })
            return new TranslatedOk("ctx.LoadedAtUtc");

        if (root is Cast { Type: SsisType.WStr or SsisType.Str, Arg1: int width } topCast)
        {
            var inner = TranslateValue(topCast.Operand, references);
            if (inner is NotTranslatable) return inner;

            var expr = ((TranslatedOk)inner).CSharpExpression;
            return new TranslatedOk($"WidthGuard.Wstr({expr}, {width}, nameof({entityName}.{columnName}), ctx.RowNumber)");
        }

        return TranslateValue(root, references);
    }

    private static TranslatedExpression TranslateValue(
        ExprNode node, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references) =>
        node is BinaryExpr { Op: BinaryOp.Add }
            ? TranslateConcat(node, references)
            : TranslateScalar(node, references);

    /// <summary>Flattens a left-associative chain of string `+` into one interpolated string
    /// literal, e.g. FirstName + " " + LastName -> $"{row.FirstName} {row.LastName}" -- matches
    /// the plan's own table row for BinaryExpr(Add) over strings.</summary>
    private static TranslatedExpression TranslateConcat(
        ExprNode addChain, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references)
    {
        var parts = new List<ExprNode>();
        Flatten(addChain, parts);

        var sb = new StringBuilder("$\"");
        foreach (var part in parts)
        {
            if (part is StringLiteral literal)
            {
                sb.Append(EscapeInterpolatedLiteral(literal.Value));
                continue;
            }

            var value = TranslateScalar(part, references);
            if (value is NotTranslatable) return value;
            sb.Append('{').Append(((TranslatedOk)value).CSharpExpression).Append('}');
        }
        sb.Append('"');
        return new TranslatedOk(sb.ToString());
    }

    private static void Flatten(ExprNode node, List<ExprNode> parts)
    {
        if (node is BinaryExpr { Op: BinaryOp.Add } add)
        {
            Flatten(add.Left, parts);
            Flatten(add.Right, parts);
        }
        else
        {
            parts.Add(node);
        }
    }

    private static TranslatedExpression TranslateScalar(
        ExprNode node, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references) => node switch
    {
        Reference r => references.TryGetValue(r.Name, out var resolved)
            ? new TranslatedOk(resolved.CSharpExpression)
            : new NotTranslatable($"reference '{r.Name}' has no resolvable producing column"),

        StringLiteral s => new TranslatedOk(CSharpStringLiteral(s.Value)),

        // Added for Conditional Split condition translation (e.g. the "1000" in
        // Amount > 1000) -- no evidenced Derived Column expression needed a bare integer
        // literal as a VALUE before this, only as SUBSTRING's start/length arguments (handled
        // separately below, never through this switch).
        IntLiteral i => new TranslatedOk(i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),

        FunctionCall { Name: "UPPER", Args.Count: 1 } upper => WrapOneArgFunction("SsisFn.Upper", upper.Args[0], references),

        FunctionCall { Name: "SUBSTRING", Args: [var strArg, IntLiteral start, IntLiteral length] } =>
            TranslateSubstring(strArg, start.Value, length.Value, references),

        Cast { Type: SsisType.WStr or SsisType.Str } cast => TranslateIntToStringCast(cast, references),

        _ => new NotTranslatable($"unsupported expression shape: {node.GetType().Name}"),
    };

    /// <summary>
    /// Translates one Conditional Split case's boolean expression (e.g. "Amount &gt; 1000") into
    /// a C# boolean expression. Deliberately narrower than full SSIS semantics -- see CLAUDE.md's
    /// "Three new component types" section for the two accepted limitations: comparisons/&amp;&amp;/||
    /// are ordinary two-valued C# logic, not SSIS's three-valued NULL-aware logic (a NULL-
    /// producing operand degrades to <see cref="NotTranslatable"/>, never guessed), and string
    /// comparison semantics are exactly the oracle-verified rules from
    /// Ssis.Runtime.Expressions' own measured corpus: == / != are ordinal, &lt;/&gt;/&lt;=/&gt;=
    /// are culture-aware.
    /// </summary>
    public static TranslatedExpression TranslateCondition(
        ExprNode root, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references) => root switch
    {
        BinaryExpr { Op: BinaryOp.And } and => TranslateLogical(and, "&&", references),
        BinaryExpr { Op: BinaryOp.Or } or => TranslateLogical(or, "||", references),
        BinaryExpr { Op: BinaryOp.Eq or BinaryOp.NotEq or BinaryOp.Lt or BinaryOp.Gt or BinaryOp.Le or BinaryOp.Ge } cmp =>
            TranslateComparison(cmp, references),
        UnaryExpr { Op: UnaryOp.Not } not => TranslateNot(not, references),
        FunctionCall { Name: "ISNULL", Args.Count: 1 } isNull => TranslateIsNull(isNull, references),
        _ => new NotTranslatable($"unsupported condition shape: {root.GetType().Name} -- only comparisons, &&/||, !, and ISNULL(...) are supported"),
    };

    /// <summary>Recurses through TranslateCondition (not TranslateScalar) -- SSIS's `!` only
    /// ever negates something boolean-shaped (a comparison, ISNULL(...), another `!`, or an
    /// &&/|| chain), evidenced by the one real case that motivated this:
    /// `!ISNULL(CustomerID_i4) &amp;&amp; FINDSTRING(...) &gt; 0` (RBC_Demo_ETL's Package_Transforms,
    /// CSPLIT_Validity's "Valid" case). An operand that isn't itself a valid condition shape
    /// degrades to NotTranslatable via the recursive call, never guessed.</summary>
    private static TranslatedExpression TranslateNot(
        UnaryExpr node, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references)
    {
        var inner = TranslateCondition(node.Operand, references);
        return inner is NotTranslatable ? inner : new TranslatedOk($"!({((TranslatedOk)inner).CSharpExpression})");
    }

    /// <summary>`ISNULL(x)` used directly as a condition (as opposed to `ISNULL(x) ? a : b`,
    /// not evidenced/supported here) -- translates its argument as a plain value and tests it
    /// for C# `null`, which is correct regardless of whether the generated row property ends
    /// up a reference type or a nullable value type.</summary>
    private static TranslatedExpression TranslateIsNull(
        FunctionCall node, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references)
    {
        var inner = TranslateScalar(node.Args[0], references);
        return inner is NotTranslatable ? inner : new TranslatedOk($"({((TranslatedOk)inner).CSharpExpression} is null)");
    }

    private static TranslatedExpression TranslateLogical(
        BinaryExpr node, string csharpOp, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references)
    {
        var left = TranslateCondition(node.Left, references);
        if (left is NotTranslatable) return left;
        var right = TranslateCondition(node.Right, references);
        if (right is NotTranslatable) return right;
        return new TranslatedOk($"({((TranslatedOk)left).CSharpExpression} {csharpOp} {((TranslatedOk)right).CSharpExpression})");
    }

    private static TranslatedExpression TranslateComparison(
        BinaryExpr cmp, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references)
    {
        var left = TranslateScalar(cmp.Left, references);
        if (left is NotTranslatable) return left;
        var right = TranslateScalar(cmp.Right, references);
        if (right is NotTranslatable) return right;

        var leftExpr = ((TranslatedOk)left).CSharpExpression;
        var rightExpr = ((TranslatedOk)right).CSharpExpression;

        // Either operand's own resolvable type decides string-vs-numeric comparison shape --
        // whichever side is a Reference/literal with a known type; a comparison between two
        // untyped shapes (e.g. two function calls) has nothing to anchor this decision to.
        var operandType = TryResolveOperandType(cmp.Left, references) ?? TryResolveOperandType(cmp.Right, references);
        if (operandType is null)
            return new NotTranslatable($"comparison '{cmp.Op}' has no operand with a resolvable type -- cannot determine string-vs-numeric comparison semantics");

        if (operandType is not (SsisType.WStr or SsisType.Str))
        {
            var op = cmp.Op switch
            {
                BinaryOp.Eq => "==",
                BinaryOp.NotEq => "!=",
                BinaryOp.Lt => "<",
                BinaryOp.Gt => ">",
                BinaryOp.Le => "<=",
                BinaryOp.Ge => ">=",
                _ => throw new InvalidOperationException($"unreachable: {cmp.Op}"),
            };
            return new TranslatedOk($"({leftExpr} {op} {rightExpr})");
        }

        return cmp.Op switch
        {
            BinaryOp.Eq => new TranslatedOk($"string.Equals({leftExpr}, {rightExpr}, System.StringComparison.Ordinal)"),
            BinaryOp.NotEq => new TranslatedOk($"!string.Equals({leftExpr}, {rightExpr}, System.StringComparison.Ordinal)"),
            BinaryOp.Lt => new TranslatedOk($"(string.Compare({leftExpr}, {rightExpr}, System.StringComparison.InvariantCulture) < 0)"),
            BinaryOp.Gt => new TranslatedOk($"(string.Compare({leftExpr}, {rightExpr}, System.StringComparison.InvariantCulture) > 0)"),
            BinaryOp.Le => new TranslatedOk($"(string.Compare({leftExpr}, {rightExpr}, System.StringComparison.InvariantCulture) <= 0)"),
            BinaryOp.Ge => new TranslatedOk($"(string.Compare({leftExpr}, {rightExpr}, System.StringComparison.InvariantCulture) >= 0)"),
            _ => throw new InvalidOperationException($"unreachable: {cmp.Op}"),
        };
    }

    private static SsisType? TryResolveOperandType(
        ExprNode node, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references) => node switch
    {
        Reference r => references.TryGetValue(r.Name, out var resolved) ? resolved.Type : null,
        StringLiteral => SsisType.WStr,
        IntLiteral => SsisType.I4,
        _ => null,
    };

    private static TranslatedExpression WrapOneArgFunction(
        string csharpFunction, ExprNode arg, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references)
    {
        var inner = TranslateScalar(arg, references);
        return inner is NotTranslatable ? inner : new TranslatedOk($"{csharpFunction}({((TranslatedOk)inner).CSharpExpression})");
    }

    private static TranslatedExpression TranslateSubstring(
        ExprNode strArg, long start, long length, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references)
    {
        var inner = TranslateScalar(strArg, references);
        return inner is NotTranslatable
            ? inner
            : new TranslatedOk($"SsisFn.Substring({((TranslatedOk)inner).CSharpExpression}, {start}, {length})");
    }

    /// <summary>
    /// Only the evidenced nested-cast shape: (DT_WSTR/DT_STR,n) applied directly to an
    /// integral reference, e.g. (DT_WSTR,10)EmployeeID -> SsisFn.Str(row.EmployeeID). This is
    /// deliberately narrower than the top-level cast handled in TranslateColumn -- a nested
    /// int-to-string cast carries no width-guard semantics of its own in either real PoC
    /// package (only the outer, whole-column cast does), so it maps to a plain conversion.
    /// </summary>
    private static TranslatedExpression TranslateIntToStringCast(
        Cast cast, IReadOnlyDictionary<string, (string CSharpExpression, SsisType Type)> references)
    {
        if (cast.Operand is Reference r && references.TryGetValue(r.Name, out var resolved) && SsisTypes.IsIntegral(resolved.Type))
            return new TranslatedOk($"SsisFn.Str({resolved.CSharpExpression})");

        return new NotTranslatable(
            "nested (DT_WSTR/DT_STR,n) cast is only supported directly over an integral reference, e.g. (DT_WSTR,10)EmployeeID");
    }

    private static string CSharpStringLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string EscapeInterpolatedLiteral(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "{{").Replace("}", "}}");
}
