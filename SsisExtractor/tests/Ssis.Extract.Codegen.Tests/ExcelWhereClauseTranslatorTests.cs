using Ssis.Extract.Codegen;
using Xunit;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>Grammar-level tests for <see cref="ExcelWhereClauseTranslator"/>, gap-audit Phase
/// 3.4 (2026-09-02). End-to-end wiring (a real fixture, generated, built, run against
/// .\SQLFORPOC_2022) is covered by PackageGeneratorTests' own
/// Generate_WiresAnExcelSourceInSqlCommandMode_WithAWhereClause -- these tests are narrower,
/// exercising the parser/translator in isolation against a plain column-type dictionary rather
/// than a full PipelineComponentSpec/PackageSpec.</summary>
public sealed class ExcelWhereClauseTranslatorTests
{
    private static readonly Dictionary<string, string> Columns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CustomerID"] = "double",
        ["DripEligible"] = "string",
        ["ReviewedBy"] = "string",
    };

    [Fact]
    public void Translate_ProducesAnAndChain_ForMultipleConditions()
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("CustomerID > 5 AND DripEligible = 'Y'", Columns);

        Assert.Null(gap);
        Assert.Equal(
            "row.CustomerID > 5 && string.Equals(row.DripEligible, \"Y\", System.StringComparison.OrdinalIgnoreCase)",
            expr);
    }

    [Theory]
    [InlineData("=", "string.Equals(row.DripEligible, \"Y\", System.StringComparison.OrdinalIgnoreCase)")]
    [InlineData("<>", "!string.Equals(row.DripEligible, \"Y\", System.StringComparison.OrdinalIgnoreCase)")]
    [InlineData("<", "string.Compare(row.DripEligible, \"Y\", System.StringComparison.OrdinalIgnoreCase) < 0")]
    [InlineData(">", "string.Compare(row.DripEligible, \"Y\", System.StringComparison.OrdinalIgnoreCase) > 0")]
    [InlineData("<=", "string.Compare(row.DripEligible, \"Y\", System.StringComparison.OrdinalIgnoreCase) <= 0")]
    [InlineData(">=", "string.Compare(row.DripEligible, \"Y\", System.StringComparison.OrdinalIgnoreCase) >= 0")]
    public void Translate_TranslatesEveryStringOperator_AsCaseInsensitive(string op, string expected)
    {
        // Case-insensitivity was directly measured only for '=' (a real raw System.Data.OleDb
        // probe: WHERE DripEligible = 'y' matched the same rows as = 'Y') -- extended uniformly
        // to the whole operator family per this type's own doc comment (Jet/ACE's Text
        // comparison mode is a single database-level property, not per-operator).
        var (expr, gap) = ExcelWhereClauseTranslator.Translate($"DripEligible {op} 'Y'", Columns);

        Assert.Null(gap);
        Assert.Equal(expected, expr);
    }

    [Theory]
    [InlineData("=", "==")]
    [InlineData("<>", "!=")]
    [InlineData("<", "<")]
    [InlineData(">", ">")]
    [InlineData("<=", "<=")]
    [InlineData(">=", ">=")]
    public void Translate_TranslatesEveryNumericOperator(string sqlOp, string csOp)
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate($"CustomerID {sqlOp} 5", Columns);

        Assert.Null(gap);
        Assert.Equal($"row.CustomerID {csOp} 5", expr);
    }

    [Fact]
    public void Translate_ReportsAGap_ForAnOrCondition()
    {
        // Real (measured via the same probe -- OR genuinely filters correctly in ACE OLEDB), but
        // deliberately out of this round's scope: only an AND-chain is supported. An OR must
        // gap, never be silently mistranslated as AND.
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("DripEligible = 'Y' OR DripEligible = 'N'", Columns);

        Assert.Null(expr);
        Assert.Contains("cannot translate", gap);
    }

    [Fact]
    public void Translate_ReportsAGap_ForAParenthesizedCondition()
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("(CustomerID > 5)", Columns);

        Assert.Null(expr);
        Assert.Contains("cannot translate", gap);
    }

    [Fact]
    public void Translate_ReportsAGap_ForAFunctionCall()
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("UCASE(DripEligible) = 'Y'", Columns);

        Assert.Null(expr);
        Assert.Contains("cannot translate", gap);
    }

    [Fact]
    public void Translate_ReportsAGap_ForAnUnknownColumn()
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("NotAColumn = 'x'", Columns);

        Assert.Null(expr);
        Assert.Contains("referencing column 'NotAColumn'", gap);
        Assert.Contains("not one of this worksheet's own resolved output columns", gap);
    }

    [Fact]
    public void Translate_ReportsAGap_WhenAStringColumnIsComparedAgainstANumericLiteral()
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("DripEligible > 5", Columns);

        Assert.Null(expr);
        Assert.Contains("CLR type 'string'", gap);
        Assert.Contains("numeric literal", gap);
    }

    [Fact]
    public void Translate_ReportsAGap_WhenANumericColumnIsComparedAgainstAStringLiteral()
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("CustomerID = 'five'", Columns);

        Assert.Null(expr);
        Assert.Contains("CLR type 'double'", gap);
        Assert.Contains("string literal", gap);
    }

    [Fact]
    public void Translate_ResolvesAColumnName_CaseInsensitively()
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("customerid > 5", Columns);

        Assert.Null(gap);
        Assert.Equal("row.customerid > 5", expr);
    }

    [Fact]
    public void Translate_ResolvesABracketedColumnName()
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("[CustomerID] > 5", Columns);

        Assert.Null(gap);
        Assert.Equal("row.CustomerID > 5", expr);
    }

    [Fact]
    public void Translate_UnescapesADoubledSingleQuote_InAStringLiteral()
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("ReviewedBy = 'O''Brien'", Columns);

        Assert.Null(gap);
        Assert.Equal(
            "string.Equals(row.ReviewedBy, \"O'Brien\", System.StringComparison.OrdinalIgnoreCase)",
            expr);
    }

    [Fact]
    public void Translate_AcceptsANegativeNumericLiteral()
    {
        var (expr, gap) = ExcelWhereClauseTranslator.Translate("CustomerID > -5", Columns);

        Assert.Null(gap);
        Assert.Equal("row.CustomerID > -5", expr);
    }
}
