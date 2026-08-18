namespace Ssis.Runtime.Expressions;

/// <summary>
/// Raised for anything the real SSIS evaluator would also fail on -- a parse error, an
/// operator applied to incompatible types (SSIS never implicitly coerces string&lt;-&gt;number;
/// see docs/gate2-schema.md's "no implicit coercion" rule), an out-of-range SUBSTRING/LEFT/
/// RIGHT argument, an unparsable cast, division by zero, or an unresolved identifier. Callers
/// generating test cases (<c>ssisx testgen</c>) catch this to assert "this input fails", the
/// same way the oracle's own ERROR rows are the expected outcome, not a bug.
/// </summary>
public sealed class SsisExpressionError(string message) : Exception(message);
