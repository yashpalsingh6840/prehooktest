namespace Ssis.Runtime.Expressions;

/// <summary>Public entry point: parse + evaluate an SSIS expression in one call.</summary>
public static class SsisExpression
{
    private static readonly Dictionary<string, SsisValue> EmptyEnv = [];

    /// <summary>
    /// Parses and evaluates <paramref name="expression"/>. <paramref name="env"/> resolves
    /// bare identifiers (Derived Column's <c>FriendlyExpression</c> form, e.g. "FirstName")
    /// and <c>@[Namespace::Name]</c> references, keyed exactly as they appear in the
    /// expression text. Throws <see cref="SsisExpressionError"/> for anything the real SSIS
    /// evaluator would also fail on.
    /// </summary>
    public static SsisValue Evaluate(string expression, IReadOnlyDictionary<string, SsisValue>? env = null) =>
        Evaluator.Evaluate(Parser.Parse(expression), env ?? EmptyEnv);

    /// <summary>Parses without evaluating -- exposed for callers that only need to know the referenced identifiers, or want to evaluate the same parsed tree repeatedly with different environments.</summary>
    public static ExprNode Parse(string expression) => Parser.Parse(expression);
}
