using System.Text.RegularExpressions;
using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen;

/// <summary>
/// Translates a <c>STOCK:FORLOOP</c> (For Loop Container)'s own <c>InitExpression</c>/
/// <c>EvalExpression</c>/<c>AssignExpression</c> attributes -- confirmed real from a genuine
/// SSDT-authored package (see <c>Ssis.Extract.Model.Package.ForLoopPayload</c>'s own doc comment,
/// Phase 3 of the unsupported-component-types plan): <c>DTS:InitExpression="@Part =1"</c>,
/// <c>DTS:EvalExpression="@Part &lt;11"</c>, <c>DTS:AssignExpression="@Part = @Part + 1"</c>.
///
/// <para><b>A genuinely different lexical form, not guessed at.</b> All three attributes
/// reference the loop's own counter variable with a BARE <c>@Name</c> (e.g. <c>@Part</c>), not
/// the <c>@[Namespace::Name]</c> form every other expression surface this tool already models
/// uses -- confirmed by reading <see cref="Lexer"/> directly: <c>LexAtReference</c> requires a
/// literal <c>[</c> immediately after <c>@</c>, so a bare <c>@Part</c> would throw
/// <c>unexpected character '@'</c> if handed to <see cref="Parser.Parse(string)"/> unmodified.
/// <see cref="RewriteBareVariableReferences"/> rewrites <c>@Name</c> (not already followed by
/// <c>[</c>) to <c>@[User::Name]</c> BEFORE parsing -- a deliberate, documented scoping choice:
/// only a <c>User::</c>-namespaced variable is ever resolvable through
/// <c>PackagePlanner.BuildGuardVariableTable</c> in the first place, so assuming <c>User::</c>
/// costs nothing a correctly-scoped reference wouldn't already need. A reference that doesn't
/// resolve (wrong namespace, unknown name, unmapped variant type) still degrades to a named
/// <see cref="NotTranslatable"/> gap through the ordinary "no resolvable declared type" path --
/// never silently guessed at a different namespace.</para>
///
/// <para><b>Reuses existing translators rather than building yet another one.</b> Init/Assign
/// are both plain assignments (<c>@Part =1</c>, <c>@Part = @Part + 1</c>) -- the exact shape
/// <see cref="ExpressionTaskEmitter.TranslateAssignment"/> already handles, reused here verbatim
/// once the bare-@ rewrite has run. Eval is a boolean condition (<c>@Part &lt;11</c>) -- the
/// exact surface <see cref="ExpressionTranslator.TranslateCondition"/> already covers (ordinal
/// vs. culture-aware comparison dispatch, oracle-verified). Only the bare-@ rewrite step is
/// genuinely new; everything downstream of it is a straight reuse.</para>
/// </summary>
internal static partial class ForLoopEmitter
{
    [GeneratedRegex(@"@(?!\[)([A-Za-z_]\w*)")]
    private static partial Regex BareVariableReferencePattern();

    /// <summary>Rewrites every bare <c>@Name</c> (not already <c>@[...]</c>) into
    /// <c>@[User::Name]</c>. Exposed <c>internal</c> so it can be unit-tested in isolation from
    /// the two translation entry points below.</summary>
    internal static string RewriteBareVariableReferences(string expression) =>
        BareVariableReferencePattern().Replace(expression, "@[User::$1]");

    /// <summary>Translates an Init/Assign attribute (e.g. <c>@Part =1</c>, <c>@Part = @Part + 1</c>)
    /// via <see cref="ExpressionTaskEmitter.TranslateAssignment"/>, after rewriting the bare-@
    /// form. <paramref name="ssisVariableName"/> comes back already namespace-qualified (e.g.
    /// "User::Part"), matching every other key in <paramref name="variables"/>.</summary>
    public static TranslatedExpression TranslateAssignment(
        string rawExpression, IReadOnlyDictionary<string, ColumnReference> variables, out string? ssisVariableName) =>
        ExpressionTaskEmitter.TranslateAssignment(RewriteBareVariableReferences(rawExpression), variables, out ssisVariableName);

    /// <summary>Translates an EvalExpression (e.g. <c>@Part &lt;11</c>) into a C# boolean
    /// expression via <see cref="ExpressionTranslator.TranslateCondition"/>, after rewriting the
    /// bare-@ form and parsing it as a whole (unlike Init/Assign, there is no top-level
    /// assignment to split off).</summary>
    public static TranslatedExpression TranslateCondition(
        string rawExpression, IReadOnlyDictionary<string, ColumnReference> variables)
    {
        var rewritten = RewriteBareVariableReferences(rawExpression);
        ExprNode root;
        try
        {
            root = Parser.Parse(rewritten);
        }
        catch (Exception ex)
        {
            return new NotTranslatable($"could not be parsed ({ex.Message})");
        }

        return ExpressionTranslator.TranslateCondition(root, variables);
    }
}
