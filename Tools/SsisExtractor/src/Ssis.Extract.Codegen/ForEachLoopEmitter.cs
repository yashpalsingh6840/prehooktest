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
}
