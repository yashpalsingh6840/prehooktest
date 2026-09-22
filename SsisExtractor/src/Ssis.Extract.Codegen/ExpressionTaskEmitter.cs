using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Translates <c>Microsoft.ExpressionTask</c>'s own <c>Expression</c> attribute -- a single
/// control-flow-level ASSIGNMENT, e.g. <c>@[User::TargetETLCutoffTime] = DATEADD("Minute",-5,
/// GETUTCDATE())</c> (the real evidenced call, <c>DailyETLMain.dtsx</c>'s own
/// "Calculate ETL Cutoff Time backup" task) -- into a target variable name plus a C# value
/// expression, for <c>Etl.Core.Pipeline.ExpressionTaskStep</c>'s own
/// <c>Action&lt;PackageVariables&gt;</c> body.
///
/// <para><b>Why this is a separate, much smaller translator than
/// <see cref="ExpressionTranslator"/> rather than a reuse of it -- same reasoning as
/// <see cref="ForEachLoopEmitter"/>'s own doc comment, extended to a second axis.</b>
/// <see cref="ExpressionTranslator.TranslateColumn"/>/<c>TranslateScalar</c> resolve a bare
/// <c>Reference</c> against a per-flow pipeline-buffer dictionary and render
/// <c>GETUTCDATE()</c>/<c>GETDATE()</c> as <c>ctx.LoadedAtUtc</c> -- a per-ROW Data Flow Task
/// concept. An ExpressionTask runs once, at a specific point in the CONTROL FLOW, with no row
/// and no <c>ctx</c> in scope at all, so reusing that dispatch verbatim would emit
/// uncompilable code the moment <c>GETUTCDATE()</c>/<c>GETDATE()</c> appears anywhere in the
/// expression -- including NESTED inside another call's argument (DATEADD's own third
/// argument, the real evidenced shape), which a root-level-only special case would not catch.
/// This translator instead renders them as <c>DateTime.UtcNow</c>/<c>DateTime.Now</c>,
/// evaluated live at the moment the task actually runs -- arguably a MORE faithful translation
/// of SSIS's own live per-execution evaluation than a per-row constant would be.</para>
///
/// <para>Scoped deliberately to just the shapes evidenced across the packages this was built
/// against (<c>DailyETLMain.dtsx</c>, Phases 2 and 6; <c>Package.dtsx</c>'s own "For Loop
/// Container" AssignExpression, Phase 3 -- see <see cref="ForLoopEmitter"/>, which reuses this
/// translator wholesale after rewriting its own bare-@ variable form): <see cref="StringLiteral"/>,
/// <see cref="IntLiteral"/>, negation (<see cref="UnaryOp.Negate"/>), a bare <see cref="Reference"/>
/// to ANOTHER package variable (resolved the same "packageVariables.GetRequired&lt;T&gt;" way a
/// conditional precedence-constraint guard already reads one, see
/// <c>PackagePlanner.TranslateGuardExpression</c> -- not evidenced in this specific task type, but
/// the identical mechanism, so reusing it costs nothing new), <c>GETUTCDATE()</c>/<c>GETDATE()</c>,
/// <c>DATEADD</c> (reusing <see cref="ExpressionTranslator.DateAddMethodNames"/> directly for every
/// datepart except "Millisecond"/"ms", the same oracle-verified mapping a Derived Column's own
/// DATEADD usage would use, so the two can never disagree on what a given datepart means),
/// <c>DATEPART</c> (only "Millisecond"/"ms", added Phase 6 -- see <see cref="TranslateDatePart"/>),
/// and NUMERIC <c>+</c>/<c>-</c> (Phase 3's own real evidenced need for '+': incrementing a loop
/// counter, <c>@Part = @Part + 1</c>; Phase 6's own real evidenced need for '-':
/// <c>0 - DATEPART("Millisecond", @X)</c>, DailyETLMain's own "Trim Any Milliseconds" task --
/// deliberately NOT string concatenation for either operator, which is unevidenced for a
/// control-flow-level assignment and stays a named gap rather than a guess). Anything else
/// degrades to <see cref="NotTranslatable"/> rather than a guess, the same "gaps not guesses"
/// rule every other translator in this tool follows.</para>
/// </summary>
public static class ExpressionTaskEmitter
{
    /// <summary>One assignment: the raw SSIS variable name on the left (e.g.
    /// "User::TargetETLCutoffTime") and the translated C# value expression on the right.</summary>
    public sealed record AssignmentTranslation(string SsisVariableName, string CSharpValueExpression);

    /// <summary>
    /// Splits <paramref name="rawExpression"/> on its one top-level <c>=</c> (SSIS's own
    /// assignment operator -- a single, standalone <c>=</c>, never <c>==</c>/<c>!=</c>/<c>&lt;=</c>/
    /// <c>&gt;=</c>, none of which this expression LANGUAGE'S grammar even recognizes as a
    /// standalone token per <see cref="Lexer"/> -- confirmed by reading it directly, not
    /// assumed: every one of those four is lexed as its own two-character token, so a bare
    /// <c>=</c> appearing anywhere outside a string literal can only ever be the assignment
    /// itself), parses the left side as a bare variable reference and the right side as an
    /// ordinary expression, then translates the right side. Returns <see cref="NotTranslatable"/>
    /// (never throws) for anything that doesn't fit this exact shape.
    /// </summary>
    public static TranslatedExpression TranslateAssignment(
        string rawExpression, IReadOnlyDictionary<string, ColumnReference> variables, out string? ssisVariableName)
    {
        ssisVariableName = null;

        var split = SplitTopLevelAssignment(rawExpression);
        if (split is null)
            return new NotTranslatable(
                "does not contain a top-level '=' assignment -- expected \"@[Namespace::Var] = <expression>\"");

        var (lhsText, rhsText) = split.Value;

        ExprNode lhsNode;
        try
        {
            lhsNode = Parser.Parse(lhsText);
        }
        catch (Exception ex)
        {
            return new NotTranslatable($"could not parse the assignment's left-hand side '{lhsText}' ({ex.Message})");
        }

        if (lhsNode is not Reference lhsRef)
            return new NotTranslatable(
                $"the assignment's left-hand side '{lhsText}' is not a bare variable reference -- only \"@[Namespace::Var] = ...\" is supported");

        ExprNode rhsNode;
        try
        {
            rhsNode = Parser.Parse(rhsText);
        }
        catch (Exception ex)
        {
            return new NotTranslatable($"could not parse the assignment's right-hand side ({ex.Message})");
        }

        var translated = TranslateNode(rhsNode, variables);
        if (translated is NotTranslatable) return translated;

        ssisVariableName = lhsRef.Name;
        return translated;
    }

    /// <summary>
    /// Finds the RHS's own top-level <c>=</c> -- the only character position where a naive
    /// <c>IndexOf('=')</c> would be wrong, since the RHS can itself contain <c>=</c> inside a
    /// string literal (SSIS strings are double-quoted with <c>""</c> as the embedded-quote
    /// escape, mirroring <see cref="Lexer"/>'s own <c>LexString</c>) or as the second character
    /// of <c>==</c>/<c>!=</c>/<c>&lt;=</c>/<c>&gt;=</c> (not evidenced in a real
    /// Microsoft.ExpressionTask assignment, but a genuinely possible RHS shape, e.g. a ternary
    /// condition). A single top-level <c>=</c> is otherwise unambiguous: this expression
    /// language's own grammar has no other construct that uses it.
    /// </summary>
    internal static (string Lhs, string Rhs)? SplitTopLevelAssignment(string expr)
    {
        var inString = false;
        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];
            if (inString)
            {
                if (c == '"')
                {
                    if (i + 1 < expr.Length && expr[i + 1] == '"') { i++; continue; } // "" escape
                    inString = false;
                }
                continue;
            }

            if (c == '"') { inString = true; continue; }

            if (c != '=') continue;

            if (i + 1 < expr.Length && expr[i + 1] == '=') { i++; continue; } // "==" -- skip both
            if (i > 0 && (expr[i - 1] is '<' or '>' or '!')) continue; // second half of <=/>=/!=

            return (expr[..i].Trim(), expr[(i + 1)..].Trim());
        }

        return null;
    }

    private static TranslatedExpression TranslateNode(ExprNode node, IReadOnlyDictionary<string, ColumnReference> variables) => node switch
    {
        StringLiteral s => new TranslatedOk(ProgramEmitter.CSharpStringLiteral(s.Value)),

        IntLiteral i => new TranslatedOk(i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),

        UnaryExpr { Op: UnaryOp.Negate } negate => TranslateNode(negate.Operand, variables) switch
        {
            TranslatedOk ok => new TranslatedOk($"-({ok.CSharpExpression})"),
            var other => other,
        },

        Reference r when variables.TryGetValue(r.Name, out var resolved) => new TranslatedOk(resolved.CSharpExpression),
        Reference r => new NotTranslatable($"references '@[{r.Name}]', which has no resolvable declared type"),

        FunctionCall { Name: "GETUTCDATE", Args.Count: 0 } => new TranslatedOk("DateTime.UtcNow"),
        FunctionCall { Name: "GETDATE", Args.Count: 0 } => new TranslatedOk("DateTime.Now"),

        FunctionCall { Name: "DATEADD", Args.Count: 3 } dateadd => TranslateDateAdd(dateadd, variables),

        // DATEPART -- only "Millisecond"/"ms" is oracle-verified (2026-09-17, Phase 6 of the
        // unsupported-component-types plan). Added here specifically for the real evidenced call
        // this closes: DailyETLMain.dtsx's own "Trim Any Milliseconds" task,
        // DATEADD("Millisecond", 0 - DATEPART("Millisecond", @X), @X). See TranslateDatePart's
        // own comment for the measured quantization it reproduces.
        FunctionCall { Name: "DATEPART", Args.Count: 2 } datepart => TranslateDatePart(datepart, variables),

        BinaryExpr { Op: BinaryOp.Add } add => TranslateNumericAdd(add, variables),

        // Subtraction -- added 2026-09-17, Phase 6, specifically for the real evidenced call's
        // own "0 - DATEPART(...)" shape. Scoped identically narrow to '+' above: only NUMERIC
        // operands (at least one side unambiguously an int literal, a negation of one, a
        // reference to an I2/I4/I8-typed variable, or DATEPART -- which always returns an int),
        // never string concatenation (unevidenced for '-' at this level, same reasoning as '+').
        BinaryExpr { Op: BinaryOp.Sub } sub => TranslateNumericSub(sub, variables),

        _ => new NotTranslatable(
            $"unsupported expression shape: {node.GetType().Name} -- only string/int literals, negation, a " +
            "reference to another package variable, GETUTCDATE()/GETDATE(), DATEADD, DATEPART, and numeric " +
            "'+'/'-' are supported"),
    };

    /// <summary>Added for Phase 3 (STOCK:FORLOOP's own AssignExpression, e.g. <c>@Part = @Part + 1</c>
    /// -- the real evidenced way a For Loop Container's own counter is incremented). Scoped
    /// deliberately NARROW: only NUMERIC '+' (at least one operand unambiguously an int literal or
    /// a reference to a variable whose declared type is I2/I4/I8) is supported -- string
    /// concatenation is not evidenced anywhere a control-flow-level assignment needs it (unlike
    /// <see cref="ExpressionTranslator"/>'s own pipeline-row-context translators, which DO support
    /// it for a Derived Column), so it degrades to <see cref="NotTranslatable"/> rather than a
    /// guess about which behaviour was intended.</summary>
    private static TranslatedExpression TranslateNumericAdd(BinaryExpr add, IReadOnlyDictionary<string, ColumnReference> variables)
    {
        var left = TranslateNode(add.Left, variables);
        if (left is NotTranslatable) return left;
        var right = TranslateNode(add.Right, variables);
        if (right is NotTranslatable) return right;

        if (IsNumeric(add.Left, variables) != true && IsNumeric(add.Right, variables) != true)
            return new NotTranslatable(
                "'+' is only supported between numeric operands (e.g. incrementing a loop counter) -- string concatenation is not supported here");

        return new TranslatedOk($"({((TranslatedOk)left).CSharpExpression}) + ({((TranslatedOk)right).CSharpExpression})");
    }

    /// <summary>Added 2026-09-17, Phase 6, specifically for the real evidenced call's own
    /// "0 - DATEPART(...)" shape (DailyETLMain.dtsx's "Trim Any Milliseconds" task). Scoped
    /// identically to <see cref="TranslateNumericAdd"/> -- only NUMERIC operands, never string
    /// concatenation (unevidenced for '-' at this level; SSIS's own expression grammar doesn't
    /// even define '-' between strings the way '+' overloads to concatenation).</summary>
    private static TranslatedExpression TranslateNumericSub(BinaryExpr sub, IReadOnlyDictionary<string, ColumnReference> variables)
    {
        var left = TranslateNode(sub.Left, variables);
        if (left is NotTranslatable) return left;
        var right = TranslateNode(sub.Right, variables);
        if (right is NotTranslatable) return right;

        if (IsNumeric(sub.Left, variables) != true && IsNumeric(sub.Right, variables) != true)
            return new NotTranslatable(
                "'-' is only supported between numeric operands (e.g. \"0 - DATEPART(...)\")");

        return new TranslatedOk($"({((TranslatedOk)left).CSharpExpression}) - ({((TranslatedOk)right).CSharpExpression})");
    }

    /// <summary>True/false when this node's own numeric-ness can be determined without evaluating
    /// it (a literal, a negation, a reference to a variable with a known declared type, or a
    /// DATEPART call -- which always returns an int); null when it can't be -- callers only need
    /// ONE side to resolve true to treat the whole expression as numeric addition/subtraction.</summary>
    private static bool? IsNumeric(ExprNode node, IReadOnlyDictionary<string, ColumnReference> variables) => node switch
    {
        IntLiteral => true,
        StringLiteral => false,
        UnaryExpr { Op: UnaryOp.Negate } u => IsNumeric(u.Operand, variables),
        Reference r when variables.TryGetValue(r.Name, out var resolved) => resolved.Type is SsisType.I2 or SsisType.I4 or SsisType.I8,
        FunctionCall { Name: "DATEPART" } => true,
        _ => null,
    };

    /// <summary>Mirrors <c>ExpressionTranslator.TranslateDateAdd</c> exactly (same measured
    /// datepart-&gt;method mapping, same literal-datepart requirement, same "Millisecond"/"ms"
    /// special case routed to <c>SsisFn.DateAddMillisecond</c> instead of a plain BCL method --
    /// see that translator's own comment for why), differing only in resolving nested
    /// references/GETUTCDATE/GETDATE through THIS translator's own
    /// <see cref="TranslateNode"/> rather than the pipeline-row-coupled one.</summary>
    private static TranslatedExpression TranslateDateAdd(FunctionCall node, IReadOnlyDictionary<string, ColumnReference> variables)
    {
        if (node.Args[0] is not StringLiteral { Value: var part })
            return new NotTranslatable(
                "DATEADD is only supported with a literal date part of \"Minute\"/\"mi\", \"Day\", \"Hour\", " +
                "\"Month\", \"Year\", \"Second\", or \"Millisecond\"/\"ms\" -- the only ones oracle-verified against the real evaluator");

        var number = TranslateNode(node.Args[1], variables);
        if (number is NotTranslatable) return number;
        var date = TranslateNode(node.Args[2], variables);
        if (date is NotTranslatable) return date;

        var numberExpr = ((TranslatedOk)number).CSharpExpression;
        var dateExpr = ((TranslatedOk)date).CSharpExpression;

        if (string.Equals(part, "Millisecond", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, "ms", StringComparison.OrdinalIgnoreCase))
            return new TranslatedOk($"SsisFn.DateAddMillisecond({dateExpr}, {numberExpr})");

        if (!ExpressionTranslator.DateAddMethodNames.TryGetValue(part, out var method))
            return new NotTranslatable(
                "DATEADD is only supported with a literal date part of \"Minute\"/\"mi\", \"Day\", \"Hour\", " +
                "\"Month\", \"Year\", \"Second\", or \"Millisecond\"/\"ms\" -- the only ones oracle-verified against the real evaluator");

        return new TranslatedOk($"{dateExpr}.{method}({numberExpr})");
    }

    /// <summary>DATEPART -- only "Millisecond"/"ms" is oracle-verified (2026-09-17, Phase 6),
    /// mirroring <c>ExpressionTranslator.TranslateDatePart</c>'s own reasoning and routing to the
    /// SAME <c>SsisFn.DatePartMillisecond</c> helper, so the two can never disagree.</summary>
    private static TranslatedExpression TranslateDatePart(FunctionCall node, IReadOnlyDictionary<string, ColumnReference> variables)
    {
        if (node.Args[0] is not StringLiteral { Value: var part }
            || (!string.Equals(part, "Millisecond", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(part, "ms", StringComparison.OrdinalIgnoreCase)))
            return new NotTranslatable(
                "DATEPART is only supported with the literal date part \"Millisecond\"/\"ms\" -- the only one oracle-verified against the real evaluator");

        var date = TranslateNode(node.Args[1], variables);
        if (date is NotTranslatable) return date;

        return new TranslatedOk($"SsisFn.DatePartMillisecond({((TranslatedOk)date).CSharpExpression})");
    }
}
