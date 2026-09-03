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

/// <summary>One entry in the <c>references</c> dictionary every translation method threads
/// through -- what C# expression reads this SSIS identifier, its SsisType, and (added
/// 2026-08-27, NullabilityInference) whether it's known-nullable from an ISNULL(x) usage
/// elsewhere in the same flow. <see cref="IsNullable"/> only ever affects
/// <c>TranslateScalar</c>'s own Reference case (append ".Value", trusting the caller already
/// guarded with ISNULL -- see that case's own comment) and <c>TranslateIsNull</c> (which must
/// use the RAW, un-unwrapped expression to check for null in the first place).</summary>
public sealed record ColumnReference(string CSharpExpression, SsisType Type, bool IsNullable = false);

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
        IReadOnlyDictionary<string, ColumnReference> references)
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

    /// <summary>SSIS's `+` is overloaded exactly like Ssis.Runtime.Expressions.Evaluator.Add
    /// (oracle-verified: string+string concats, numeric+numeric adds with type promotion --
    /// "1 + NULL(DT_I4)" and "(DT_WSTR,20)(1 + 2.5)" => "3.5" are both in the gate-2 corpus) --
    /// this dispatch mirrors that, deciding by whichever operand's type is resolvable, same
    /// pattern TranslateComparison already uses for string-vs-numeric comparison semantics.
    /// Defaults to string concat when NEITHER operand's type is resolvable (e.g. two bare
    /// function calls with no known return type) -- preserves every previously-evidenced concat
    /// case exactly, since none of them had a numeric operand.</summary>
    private static TranslatedExpression TranslateValue(
        ExprNode node, IReadOnlyDictionary<string, ColumnReference> references)
    {
        if (node is not BinaryExpr { Op: BinaryOp.Add } add) return TranslateScalar(node, references);

        var operandType = TryResolveOperandType(add.Left, references) ?? TryResolveOperandType(add.Right, references);
        return operandType is not null && operandType is not (SsisType.WStr or SsisType.Str)
            ? TranslateArithmeticAdd(add, references)
            : TranslateConcat(node, references);
    }

    /// <summary>Real evidenced call: SUBSTRING's own start argument, FINDSTRING(TRIM(Email),
    /// "@",1) + 1 (RBC_Demo_ETL's Package_Transforms, DER_Enrich.EmailDomain). Both sides
    /// recurse via TranslateValue (not TranslateScalar) so a chained a+b+c of numeric terms
    /// would also work, though that's not evidenced anywhere.</summary>
    private static TranslatedExpression TranslateArithmeticAdd(
        BinaryExpr node, IReadOnlyDictionary<string, ColumnReference> references)
    {
        var left = TranslateValue(node.Left, references);
        if (left is NotTranslatable) return left;
        var right = TranslateValue(node.Right, references);
        if (right is NotTranslatable) return right;
        return new TranslatedOk($"({((TranslatedOk)left).CSharpExpression} + {((TranslatedOk)right).CSharpExpression})");
    }

    /// <summary>Flattens a left-associative chain of string `+` into one interpolated string
    /// literal, e.g. FirstName + " " + LastName -> $"{row.FirstName} {row.LastName}" -- matches
    /// the plan's own table row for BinaryExpr(Add) over strings.</summary>
    private static TranslatedExpression TranslateConcat(
        ExprNode addChain, IReadOnlyDictionary<string, ColumnReference> references)
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
        ExprNode node, IReadOnlyDictionary<string, ColumnReference> references) => node switch
    {
        // A nullable-inferred column (NullabilityInference, from an ISNULL(x) usage elsewhere
        // in this same flow) is unwrapped with .Value here -- trusting that any expression
        // reaching this VALUE context is only ever evaluated after the ISNULL check that made
        // it nullable in the first place has already ruled out null (the one evidenced pattern:
        // ISNULL(x) ? fallback : f(x)). This is a deliberate, real tradeoff, not a proof: a
        // reference used with no such guard would throw at runtime instead of degrading to a
        // gap. TranslateIsNull below is the one place that must NOT go through this case, since
        // it needs the raw (still-nullable) expression to check for null at all.
        // A call-expression reference (e.g. a Data Conversion column's own
        // "SsisFn.ToNullableX(row.Y)", see TranslateDataConversion) is evaluated FRESH here,
        // independently of whatever ISNULL(...) check guarded this value context -- Roslyn's
        // nullable flow analysis narrows a plain member-access chain like "row.Y" across two
        // syntactically-identical checks, but never a method call (no purity assumption), so
        // ".Value" alone left CS8629 as a hard build error under TreatWarningsAsErrors (caught
        // by actually building the generated project, not by unit tests). The null-forgiving
        // "!" is semantically justified, not a suppression of a real risk: the ISNULL guard that
        // put this reference in a non-null branch calls the exact same pure, deterministic
        // function with the exact same input, so it cannot return null here. A plain row-property
        // reference never ends in ')' and is left exactly as before (already correctly narrowed
        // by the compiler, confirmed by the pre-existing nullable-columns golden text).
        Reference r => references.TryGetValue(r.Name, out var resolved)
            ? new TranslatedOk(resolved.IsNullable
                ? $"{resolved.CSharpExpression}{(resolved.CSharpExpression.EndsWith(')') ? "!" : "")}.Value"
                : resolved.CSharpExpression)
            : new NotTranslatable($"reference '{r.Name}' has no resolvable producing column"),

        StringLiteral s => new TranslatedOk(CSharpStringLiteral(s.Value)),

        // Added for Conditional Split condition translation (e.g. the "1000" in
        // Amount > 1000) -- no evidenced Derived Column expression needed a bare integer
        // literal as a VALUE before this, only as SUBSTRING's start/length arguments (handled
        // separately below, never through this switch).
        IntLiteral i => new TranslatedOk(i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),

        // Mirrors TranslateColumn's own top-level GETUTCDATE/GETDATE check -- that one only
        // ever fires when the call IS the whole column expression; this covers the same call
        // NESTED inside another expression, e.g. DATEDIFF("dd",SignupDate_dt,GETDATE())'s own
        // end-date argument (RBC_Demo_ETL's Package_Transforms, DER_Enrich.TenureDays).
        FunctionCall { Name: "GETUTCDATE" or "GETDATE", Args.Count: 0 } => new TranslatedOk("ctx.LoadedAtUtc"),

        FunctionCall { Name: "UPPER", Args.Count: 1 } upper => WrapOneArgFunction("SsisFn.Upper", upper.Args[0], references),

        FunctionCall { Name: "TRIM", Args.Count: 1 } trim => WrapOneArgFunction("SsisFn.Trim", trim.Args[0], references),

        // Args translated recursively via TranslateValue, not required to be literals -- the
        // real evidenced call needs a computed start, SUBSTRING(TRIM(Email),
        // FINDSTRING(TRIM(Email),"@",1) + 1, 100) (same package/column as FINDSTRING's own
        // comment above). A plain literal start/length (the only shape evidenced before this)
        // still translates identically -- TranslateScalar's own IntLiteral case renders the same
        // text either way, so no existing golden output changes.
        FunctionCall { Name: "SUBSTRING", Args.Count: 3 } substring => TranslateSubstring(substring, references),

        // Args are translated recursively (not required to be literals, unlike SUBSTRING's
        // start/length) because the one real evidenced call always nests another function --
        // FINDSTRING(TRIM(Email),"@",1) (RBC_Demo_ETL's Package_Transforms, CSPLIT_Validity).
        FunctionCall { Name: "FINDSTRING", Args.Count: 3 } findstring => TranslateFindString(findstring, references),

        // Only "dd" -- the one datepart oracle-verified against the real evaluator
        // (Ssis.Runtime.Expressions.Functions.DateDiff makes the identical restriction; see its
        // own doc comment for the measured semantics: day-BOUNDARY crossings, not elapsed
        // 24-hour periods, endDate - startDate). Real evidenced call:
        // DATEDIFF("dd",SignupDate_dt,GETDATE()) (RBC_Demo_ETL's Package_Transforms,
        // DER_Enrich.TenureDays). Any other datepart literal degrades rather than guesses.
        FunctionCall { Name: "DATEDIFF", Args.Count: 3 } datediff => TranslateDateDiff(datediff, references),

        Cast { Type: SsisType.WStr or SsisType.Str } cast => TranslateIntToStringCast(cast, references),

        // Ternary (cond ? whenTrue : whenFalse) -- the AST/evaluator already exist and are
        // oracle-verified (Ssis.Runtime.Expressions.Ast.Conditional, gate 2's own corpus rows
        // 23-24: "TRUE ? "a" : "b"" and the NULL-condition case), this just wires them into C#.
        // The condition reuses TranslateCondition verbatim -- same comparisons/&&/||/!/ISNULL
        // surface a Conditional Split case gets, nothing new. Real evidenced call:
        // FINDSTRING(TRIM(Email),"@",1) > 0 ? SUBSTRING(...) : "(none)" (RBC_Demo_ETL's
        // Package_Transforms, DER_Enrich.EmailDomain).
        Conditional ternary => TranslateTernary(ternary, references),

        // Confirmed the parser never folds this into a literal (-1 is always
        // UnaryExpr(Negate, IntLiteral(1)), per Parser.cs) -- real evidenced need: TenureDays'
        // own "ISNULL(SignupDate_dt) ? -1 : DATEDIFF(...)". Pure numeric negation, oracle-
        // verified (Ssis.Runtime.Expressions.Evaluator's own Negate case; corpus row "-5 % 3").
        // Unlike BinaryOp.Add, negation has no string-vs-numeric ambiguity to resolve.
        UnaryExpr { Op: UnaryOp.Negate } negate => TranslateNegate(negate, references),

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
        ExprNode root, IReadOnlyDictionary<string, ColumnReference> references) => root switch
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
        UnaryExpr node, IReadOnlyDictionary<string, ColumnReference> references)
    {
        var inner = TranslateCondition(node.Operand, references);
        return inner is NotTranslatable ? inner : new TranslatedOk($"!({((TranslatedOk)inner).CSharpExpression})");
    }

    /// <summary>`ISNULL(x)` used directly as a condition -- e.g. `ISNULL(SignupDate) ? -1 :
    /// DATEDIFF(...)`. A bare Reference argument (every evidenced case) is resolved directly
    /// against the RAW, still-possibly-nullable expression -- NOT through TranslateScalar's own
    /// Reference case, which would unwrap a nullable-inferred column with `.Value` and defeat
    /// the null check this function exists to perform. Any other argument shape falls back to
    /// TranslateScalar (unaffected by this distinction, since no evidenced function call
    /// currently returns a nullable type).</summary>
    private static TranslatedExpression TranslateIsNull(
        FunctionCall node, IReadOnlyDictionary<string, ColumnReference> references)
    {
        if (node.Args[0] is Reference r)
        {
            return references.TryGetValue(r.Name, out var resolved)
                ? new TranslatedOk($"({resolved.CSharpExpression} is null)")
                : new NotTranslatable($"reference '{r.Name}' has no resolvable producing column");
        }

        var inner = TranslateScalar(node.Args[0], references);
        return inner is NotTranslatable ? inner : new TranslatedOk($"({((TranslatedOk)inner).CSharpExpression} is null)");
    }

    private static TranslatedExpression TranslateLogical(
        BinaryExpr node, string csharpOp, IReadOnlyDictionary<string, ColumnReference> references)
    {
        var left = TranslateCondition(node.Left, references);
        if (left is NotTranslatable) return left;
        var right = TranslateCondition(node.Right, references);
        if (right is NotTranslatable) return right;
        return new TranslatedOk($"({((TranslatedOk)left).CSharpExpression} {csharpOp} {((TranslatedOk)right).CSharpExpression})");
    }

    private static TranslatedExpression TranslateComparison(
        BinaryExpr cmp, IReadOnlyDictionary<string, ColumnReference> references)
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

    /// <summary>Return type per function this translator itself supports -- deliberately NOT
    /// Ssis.Runtime.Expressions.Functions.ReturnTypes (that one covers the full oracle corpus,
    /// including functions this translator can't emit yet, like DATEDIFF -- resolving one of
    /// those here would claim a type this translator can't actually produce code for).</summary>
    private static readonly Dictionary<string, SsisType> KnownFunctionReturnTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UPPER"] = SsisType.WStr,
        ["TRIM"] = SsisType.WStr,
        ["SUBSTRING"] = SsisType.WStr,
        ["FINDSTRING"] = SsisType.I4,
        ["DATEDIFF"] = SsisType.I4,
    };

    private static SsisType? TryResolveOperandType(
        ExprNode node, IReadOnlyDictionary<string, ColumnReference> references) => node switch
    {
        Reference r => references.TryGetValue(r.Name, out var resolved) ? resolved.Type : null,
        StringLiteral => SsisType.WStr,
        IntLiteral => SsisType.I4,
        FunctionCall f => KnownFunctionReturnTypes.TryGetValue(f.Name, out var t) ? t : null,
        _ => null,
    };

    private static TranslatedExpression WrapOneArgFunction(
        string csharpFunction, ExprNode arg, IReadOnlyDictionary<string, ColumnReference> references)
    {
        var inner = TranslateScalar(arg, references);
        return inner is NotTranslatable ? inner : new TranslatedOk($"{csharpFunction}({((TranslatedOk)inner).CSharpExpression})");
    }

    /// <summary>SUBSTRING(value, start, length) -- all three args translated recursively via
    /// TranslateValue (see the switch case above for why: start can be a computed arithmetic
    /// expression, not just a literal). Passed straight to SsisFn.Substring, which carries the
    /// existing 1-based/start-&lt;1-errors/clamp-at-end-of-string semantics.</summary>
    private static TranslatedExpression TranslateSubstring(
        FunctionCall node, IReadOnlyDictionary<string, ColumnReference> references) =>
        TranslateFunctionArgs("SsisFn.Substring", node.Args, references);

    /// <summary>FINDSTRING(value, search, occurrence) -- all three args translated recursively
    /// (see the switch case above for why), then passed straight to SsisFn.FindString, which
    /// carries the oracle-verified semantics (Ssis.Runtime.Expressions.Functions.FindString):
    /// occurrence &lt; 1 errors, an empty search string never matches, matches may overlap.</summary>
    private static TranslatedExpression TranslateFindString(
        FunctionCall node, IReadOnlyDictionary<string, ColumnReference> references) =>
        TranslateFunctionArgs("SsisFn.FindString", node.Args, references);

    /// <summary>DATEDIFF(datepart, start, end) -- the datepart argument must be a literal
    /// string (SSIS's own syntax requires this, same as DATEPART), checked case-insensitively
    /// against "dd" (the only measured value). start/end translated via TranslateValue so a
    /// nested GETDATE()/GETUTCDATE() (the real evidenced end-date argument) resolves through
    /// the switch case above.</summary>
    private static TranslatedExpression TranslateDateDiff(
        FunctionCall node, IReadOnlyDictionary<string, ColumnReference> references)
    {
        if (node.Args[0] is not StringLiteral { Value: var part } || !string.Equals(part, "dd", StringComparison.OrdinalIgnoreCase))
            return new NotTranslatable("DATEDIFF is only supported with the \"dd\" date part -- the only one oracle-verified against the real evaluator");

        var start = TranslateValue(node.Args[1], references);
        if (start is NotTranslatable) return start;
        var end = TranslateValue(node.Args[2], references);
        if (end is NotTranslatable) return end;

        return new TranslatedOk($"SsisFn.DateDiffDays({((TranslatedOk)start).CSharpExpression}, {((TranslatedOk)end).CSharpExpression})");
    }

    private static TranslatedExpression TranslateFunctionArgs(
        string csharpFunction, IReadOnlyList<ExprNode> args, IReadOnlyDictionary<string, ColumnReference> references)
    {
        var translatedArgs = new List<string>();
        foreach (var arg in args)
        {
            var t = TranslateValue(arg, references);
            if (t is NotTranslatable) return t;
            translatedArgs.Add(((TranslatedOk)t).CSharpExpression);
        }
        return new TranslatedOk($"{csharpFunction}({string.Join(", ", translatedArgs)})");
    }

    /// <summary>Both branches are translated via TranslateValue (not TranslateScalar directly)
    /// so a branch that's itself a concat chain would work too -- not evidenced in any real
    /// package, but costs nothing and matches how TranslateColumn's own top-level dispatch
    /// already recurses.</summary>
    private static TranslatedExpression TranslateTernary(
        Conditional node, IReadOnlyDictionary<string, ColumnReference> references)
    {
        var condition = TranslateCondition(node.Condition, references);
        if (condition is NotTranslatable) return condition;

        var whenTrue = TranslateValue(node.WhenTrue, references);
        if (whenTrue is NotTranslatable) return whenTrue;

        var whenFalse = TranslateValue(node.WhenFalse, references);
        if (whenFalse is NotTranslatable) return whenFalse;

        return new TranslatedOk(
            $"({((TranslatedOk)condition).CSharpExpression} ? {((TranslatedOk)whenTrue).CSharpExpression} : {((TranslatedOk)whenFalse).CSharpExpression})");
    }

    private static TranslatedExpression TranslateNegate(
        UnaryExpr node, IReadOnlyDictionary<string, ColumnReference> references)
    {
        var inner = TranslateScalar(node.Operand, references);
        return inner is NotTranslatable ? inner : new TranslatedOk($"-({((TranslatedOk)inner).CSharpExpression})");
    }

    /// <summary>
    /// Only the evidenced nested-cast shape: (DT_WSTR/DT_STR,n) applied directly to an
    /// integral reference, e.g. (DT_WSTR,10)EmployeeID -> SsisFn.Str(row.EmployeeID). This is
    /// deliberately narrower than the top-level cast handled in TranslateColumn -- a nested
    /// int-to-string cast carries no width-guard semantics of its own in either real PoC
    /// package (only the outer, whole-column cast does), so it maps to a plain conversion.
    /// </summary>
    private static TranslatedExpression TranslateIntToStringCast(
        Cast cast, IReadOnlyDictionary<string, ColumnReference> references)
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
