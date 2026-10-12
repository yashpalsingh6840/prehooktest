using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Translates a ForEach Loop's own per-iteration SQL text -- an ordinary SSIS expression, but
/// one living on an Execute SQL Task's <c>SqlStatementSource</c> PropertyExpression (a
/// control-flow-level string built from <c>@[Namespace::Variable]</c> references), NOT a Data
/// Flow's own Derived Column/Conditional Split expression -- into a C# expression that rebuilds
/// the same string on every iteration, substituting the current file value in place of the
/// loop's own mapped variable.
///
/// <para><b>Why this is a separate, much smaller translator than <see cref="ExpressionTranslator"/>
/// rather than a reuse of it.</b> Every existing translation method in this tool resolves a
/// <c>Reference</c> against a per-flow <c>Dictionary&lt;string, ColumnReference&gt;</c> keyed by
/// PIPELINE BUFFER COLUMN NAME (a Derived Column's own bare-identifier FriendlyExpression form,
/// e.g. "FirstName") -- there is no pipeline, no buffer, and no row here at all. A ForEach Loop's
/// own expression instead has exactly ONE resolvable reference (the loop's own single mapped
/// variable, confirmed real from RBC_Demo_ETL's own <c>FEL_SampleFiles</c>:
/// <c>"..." + @[User::CurrentFile] + "..."</c>, parsed via <c>Ssis.Runtime.Expressions.Lexer</c>'s
/// own <c>AtReference</c> token into <c>Reference("User::CurrentFile")</c> -- confirmed by
/// reading the lexer directly, not assumed), so reusing the full column-resolution machinery
/// would be strictly more complexity for strictly less capability. Scoped deliberately to just
/// three AST shapes -- <see cref="StringLiteral"/>, the loop's own <see cref="Reference"/>, and
/// string concatenation (<see cref="BinaryOp.Add"/>) -- since that is the entire real evidenced
/// surface; anything else (another variable, a function call, non-string arithmetic) degrades to
/// a <see cref="NotTranslatable"/> gap rather than a guess, the same "gaps not guesses" rule
/// every other translator in this tool follows.</para>
/// </summary>
public static class ForEachLoopEmitter
{
    /// <summary>Parses <paramref name="rawExpression"/> (the raw
    /// <c>PropertyExpression[@Name='SqlStatementSource']</c> text) and translates it into a C#
    /// expression, substituting <paramref name="csharpVariableName"/> (the generated loop's own
    /// C# local holding the current iteration's file value) wherever
    /// <paramref name="ssisVariableName"/> (e.g. <c>"User::CurrentFile"</c>) is referenced.</summary>
    public static TranslatedExpression TranslateSqlTemplate(string rawExpression, string ssisVariableName, string csharpVariableName)
    {
        ExprNode ast;
        try
        {
            ast = Parser.Parse(rawExpression);
        }
        catch (SsisExpressionError ex)
        {
            return new NotTranslatable($"could not parse ForEach Loop's own per-iteration SQL expression: {ex.Message}");
        }

        return TranslateNode(ast, ssisVariableName, csharpVariableName);
    }

    private static TranslatedExpression TranslateNode(ExprNode node, string ssisVariableName, string csharpVariableName) => node switch
    {
        StringLiteral s => new TranslatedOk(ProgramEmitter.CSharpStringLiteral(s.Value)),
        Reference r when r.Name == ssisVariableName => new TranslatedOk(csharpVariableName),
        Reference r => new NotTranslatable(
            $"references '@[{r.Name}]', which is not this loop's own mapped variable ('@[{ssisVariableName}]') -- not supported"),
        BinaryExpr { Op: BinaryOp.Add } add => TranslateAdd(add, ssisVariableName, csharpVariableName),
        _ => new NotTranslatable(
            $"unsupported expression shape: {node.GetType().Name} -- only string literals, this loop's own variable, and string concatenation ('+') are supported"),
    };

    private static TranslatedExpression TranslateAdd(BinaryExpr add, string ssisVariableName, string csharpVariableName)
    {
        var left = TranslateNode(add.Left, ssisVariableName, csharpVariableName);
        if (left is NotTranslatable) return left;

        var right = TranslateNode(add.Right, ssisVariableName, csharpVariableName);
        if (right is NotTranslatable) return right;

        return new TranslatedOk($"{((TranslatedOk)left).CSharpExpression} + {((TranslatedOk)right).CSharpExpression}");
    }

    /// <summary>
    /// Phase 2.7 (S2) of the independent-review fix plan. RBC_Demo_ETL's own real
    /// <c>Mapping/SQL_LogFileNameStatement.cs</c> builds
    /// <c>"... N'" + currentFile + "');"</c> -- a string-BUILT SQL statement, exactly like SSIS's
    /// own real behaviour, so a file name containing a single quote (e.g. <c>O'Brien.csv</c>)
    /// breaks or injects the resulting SQL, in EITHER the real SSIS package or this generated
    /// translation of it. Measured, not assumed: this is a faithful translation of what SSIS
    /// itself does (string-substitute the raw text), so "fixing" it (e.g. parameterizing the
    /// value) would make the generated code diverge from the real package's own behaviour --
    /// this is reported as a non-blocking advisory instead, never silently "fixed".
    ///
    /// <para>Detected by walking the whole <c>+</c> concatenation chain left-to-right and
    /// tracking the running count of single-quote characters contributed by every
    /// <see cref="StringLiteral"/> seen so far: an ODD count means the loop's own variable
    /// reference lands INSIDE an already-open SQL string literal (e.g. after a trailing
    /// <c>N'</c>) rather than between two complete SQL tokens. Re-parses the same expression
    /// <see cref="TranslateSqlTemplate"/> already did -- cheap and deterministic, and keeps this
    /// concern (a SQL-quoting risk) entirely separate from translation itself.</para>
    /// </summary>
    public static bool IsSplicedInsideAQuotedSqlLiteral(string rawExpression, string ssisVariableName)
    {
        ExprNode ast;
        try
        {
            ast = Parser.Parse(rawExpression);
        }
        catch (SsisExpressionError)
        {
            return false;
        }

        var quoteCountSoFar = 0;
        var spliced = false;

        void Walk(ExprNode node)
        {
            switch (node)
            {
                case StringLiteral s:
                    quoteCountSoFar += s.Value.Count(c => c == '\'');
                    break;
                case Reference r when r.Name == ssisVariableName:
                    if (quoteCountSoFar % 2 != 0) spliced = true;
                    break;
                case BinaryExpr { Op: BinaryOp.Add } add:
                    Walk(add.Left);
                    Walk(add.Right);
                    break;
            }
        }

        Walk(ast);
        return spliced;
    }
}
