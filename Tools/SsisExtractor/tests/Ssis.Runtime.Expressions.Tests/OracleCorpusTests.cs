using Ssis.Runtime.Expressions;

namespace Ssis.Runtime.Expressions.Tests;

/// <summary>
/// Verifies <c>Ssis.Runtime.Expressions</c> against every row of
/// <c>Fixtures/ssis-expression-oracle.tsv</c> -- produced by evaluating each expression with
/// the REAL SSIS 17 expression evaluator (<c>src/Ssis.Expression.Oracle</c>, Windows-only,
/// separate slnx). This is the library's actual spec: if a row here fails, either the library
/// is wrong or the corpus needs a documented divergence entry below -- never "fix" a failure
/// by changing the fixture to match the library's current output.
/// </summary>
public class OracleCorpusTests
{
    /// <summary>
    /// Expressions where this library's output deliberately does NOT match the oracle,
    /// with the reason -- see <see cref="SsisValue.ToDisplayString"/>'s own doc comment for
    /// the full investigation. Both are float division stringified via a (DT_WSTR,n) cast,
    /// where SSIS's expression compiler appears to track a "scale" through literals/
    /// arithmetic that this library does not attempt to reproduce -- none of this PoC's
    /// actual harvested expressions do this (measured surface: string concat, UPPER,
    /// SUBSTRING, GETUTCDATE, DT_WSTR casts only -- no float arithmetic at all).
    /// </summary>
    private static readonly HashSet<string> KnownDivergences =
    [
        "5.0/2",
        "(DT_WSTR,20)(5.0/2)",
    ];

    public static IEnumerable<object[]> CorpusRows() =>
        OracleCorpus.Load().Where(r => !KnownDivergences.Contains(r.Expression)).Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(CorpusRows))]
    public void MatchesOracle(OracleRow row)
    {
        switch (row.Outcome)
        {
            case "NULL":
                var nullResult = SsisExpression.Evaluate(row.Expression);
                Assert.True(nullResult.IsNull, $"expected NULL for `{row.Expression}`, got {Describe(nullResult)}");
                break;

            case "VALUE":
                var result = SsisExpression.Evaluate(row.Expression);
                Assert.False(result.IsNull, $"expected VALUE \"{row.Value}\" for `{row.Expression}`, got NULL");
                Assert.Equal(row.Value, result.ToDisplayString());
                break;

            case "ERROR":
                Assert.Throws<SsisExpressionError>(() => SsisExpression.Evaluate(row.Expression));
                break;

            default:
                throw new InvalidOperationException($"unrecognised oracle outcome '{row.Outcome}' for `{row.Expression}`");
        }
    }

    /// <summary>
    /// The known-divergence rows still have to evaluate to SOMETHING sane (not crash, not
    /// silently disagree about nullness) -- this pins that much, without asserting the exact
    /// (unreplicated) string format.
    /// </summary>
    [Fact]
    public void KnownDivergences_StillEvaluateWithoutCrashing()
    {
        foreach (var expr in KnownDivergences)
        {
            var result = SsisExpression.Evaluate(expr);
            Assert.False(result.IsNull, $"`{expr}` should still produce a value (just not the exact oracle string)");
        }
    }

    /// <summary>Guards against a known-divergence entry going stale -- if the corpus doesn't contain the expression anymore, the entry is dead weight (or worse, silently hiding a typo).</summary>
    [Fact]
    public void KnownDivergences_AreAllPresentInTheCorpus()
    {
        var corpusExpressions = OracleCorpus.Load().Select(r => r.Expression).ToHashSet();
        foreach (var expr in KnownDivergences)
        {
            Assert.Contains(expr, corpusExpressions);
        }
    }

    private static string Describe(SsisValue v) => v.IsNull ? $"NULL({v.Type})" : $"{v.Type}:{v.ToDisplayString()}";
}
