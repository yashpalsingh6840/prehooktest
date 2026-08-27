using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen.Tests;

public class ExpressionTranslatorTests
{
    private static Dictionary<string, (string CSharpExpression, SsisType Type)> Refs(
        params (string Name, string CSharpExpression, SsisType Type)[] refs) =>
        refs.ToDictionary(r => r.Name, r => (r.CSharpExpression, r.Type));

    [Fact]
    public void TranslateColumn_ProducesTheExactHandWrittenFullNameExpression()
    {
        // (DT_WSTR,101)(FirstName + " " + LastName) -- the real DER_MergeColumns.FullName expression.
        var ast = SsisExpression.Parse("(DT_WSTR,101)(FirstName + \" \" + LastName)");
        var refs = Refs(("FirstName", "row.FirstName", SsisType.WStr), ("LastName", "row.LastName", SsisType.WStr));

        var result = ExpressionTranslator.TranslateColumn(ast, "Employee", "FullName", refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal(
            "WidthGuard.Wstr($\"{row.FirstName} {row.LastName}\", 101, nameof(Employee.FullName), ctx.RowNumber)",
            ok.CSharpExpression);
    }

    [Fact]
    public void TranslateColumn_ProducesTheExactHandWrittenEmployeeKeyExpression()
    {
        // (DT_WSTR,20)(UPPER(SUBSTRING(Department,1,3)) + "-" + (DT_WSTR,10)EmployeeID)
        var ast = SsisExpression.Parse("(DT_WSTR,20)(UPPER(SUBSTRING(Department,1,3)) + \"-\" + (DT_WSTR,10)EmployeeID)");
        var refs = Refs(("Department", "row.Department", SsisType.WStr), ("EmployeeID", "row.EmployeeID", SsisType.I4));

        var result = ExpressionTranslator.TranslateColumn(ast, "Employee", "EmployeeKey", refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal(
            "WidthGuard.Wstr($\"{SsisFn.Upper(SsisFn.Substring(row.Department, 1, 3))}-{SsisFn.Str(row.EmployeeID)}\", 20, nameof(Employee.EmployeeKey), ctx.RowNumber)",
            ok.CSharpExpression);
    }

    [Fact]
    public void TranslateColumn_ProducesTheExactHandWrittenDepartmentKeyExpression()
    {
        // (DT_WSTR,25)(UPPER(DepartmentCode) + "-" + (DT_WSTR,10)DepartmentID)
        var ast = SsisExpression.Parse("(DT_WSTR,25)(UPPER(DepartmentCode) + \"-\" + (DT_WSTR,10)DepartmentID)");
        var refs = Refs(("DepartmentCode", "row.DepartmentCode", SsisType.WStr), ("DepartmentID", "row.DepartmentID", SsisType.I4));

        var result = ExpressionTranslator.TranslateColumn(ast, "Department", "DepartmentKey", refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal(
            "WidthGuard.Wstr($\"{SsisFn.Upper(row.DepartmentCode)}-{SsisFn.Str(row.DepartmentID)}\", 25, nameof(Department.DepartmentKey), ctx.RowNumber)",
            ok.CSharpExpression);
    }

    [Fact]
    public void TranslateColumn_TranslatesGetUtcDate_ToRowContextLoadedAtUtc()
    {
        var ast = SsisExpression.Parse("GETUTCDATE()");

        var result = ExpressionTranslator.TranslateColumn(ast, "Employee", "LoadedAtUtc", Refs());

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("ctx.LoadedAtUtc", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateColumn_ReportsAGap_WhenAReferenceIsUnresolved()
    {
        var ast = SsisExpression.Parse("UPPER(SomeColumnNotInLineage)");

        var result = ExpressionTranslator.TranslateColumn(ast, "Employee", "X", Refs());

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("SomeColumnNotInLineage", gap.Reason);
    }

    [Fact]
    public void TranslateColumn_ReportsAGap_RatherThanGuessing_ForAnUnsupportedFunction()
    {
        // LEN() is real SSIS syntax and the runtime evaluator supports it -- but it's outside
        // what this translator has evidence for across both real PoC packages, so it must be
        // reported, never silently mistranslated.
        var ast = SsisExpression.Parse("LEN(FirstName)");
        var refs = Refs(("FirstName", "row.FirstName", SsisType.WStr));

        var result = ExpressionTranslator.TranslateColumn(ast, "Employee", "X", refs);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("FunctionCall", gap.Reason);
    }

    [Fact]
    public void TranslateCondition_TranslatesANumericComparison_ToTheDirectCSharpOperator()
    {
        // The real SyntheticConditionalSplit.dtsx case: "Amount > 1000".
        var ast = SsisExpression.Parse("Amount > 1000");
        var refs = Refs(("Amount", "row.Amount", SsisType.Numeric));

        var result = ExpressionTranslator.TranslateCondition(ast, refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("(row.Amount > 1000)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateCondition_TranslatesStringEquality_AsOrdinal()
    {
        var ast = SsisExpression.Parse("Status == \"Active\"");
        var refs = Refs(("Status", "row.Status", SsisType.WStr));

        var result = ExpressionTranslator.TranslateCondition(ast, refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("string.Equals(row.Status, \"Active\", System.StringComparison.Ordinal)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateCondition_TranslatesStringInequality_AsNegatedOrdinal()
    {
        var ast = SsisExpression.Parse("Status != \"Active\"");
        var refs = Refs(("Status", "row.Status", SsisType.WStr));

        var result = ExpressionTranslator.TranslateCondition(ast, refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("!string.Equals(row.Status, \"Active\", System.StringComparison.Ordinal)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateCondition_TranslatesStringOrdering_AsCultureAwareCompare()
    {
        var ast = SsisExpression.Parse("Region < \"M\"");
        var refs = Refs(("Region", "row.Region", SsisType.WStr));

        var result = ExpressionTranslator.TranslateCondition(ast, refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("(string.Compare(row.Region, \"M\", System.StringComparison.InvariantCulture) < 0)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateCondition_ComposesTwoComparisons_WithLogicalAnd()
    {
        var ast = SsisExpression.Parse("Amount > 1000 && Region == \"East\"");
        var refs = Refs(("Amount", "row.Amount", SsisType.Numeric), ("Region", "row.Region", SsisType.WStr));

        var result = ExpressionTranslator.TranslateCondition(ast, refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal(
            "((row.Amount > 1000) && string.Equals(row.Region, \"East\", System.StringComparison.Ordinal))",
            ok.CSharpExpression);
    }

    [Fact]
    public void TranslateCondition_ComposesTwoComparisons_WithLogicalOr()
    {
        var ast = SsisExpression.Parse("Amount > 1000 || Amount < 0");
        var refs = Refs(("Amount", "row.Amount", SsisType.Numeric));

        var result = ExpressionTranslator.TranslateCondition(ast, refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("((row.Amount > 1000) || (row.Amount < 0))", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateCondition_ReportsAGap_ForAConditionThatIsNotAComparisonOrLogicalCombination()
    {
        // A bare reference is a valid SSIS boolean expression (a DT_BOOL column) but not one of
        // the shapes this translator has evidence for -- degrade, don't guess a truthiness rule.
        var ast = SsisExpression.Parse("IsActive");

        var result = ExpressionTranslator.TranslateCondition(ast, Refs(("IsActive", "row.IsActive", SsisType.Bool)));

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("unsupported condition shape", gap.Reason);
    }

    [Fact]
    public void TranslateCondition_ReportsAGap_WhenNeitherOperandHasAResolvableType()
    {
        // Both operands translate fine as VALUES (UPPER() resolves through Name/Other), but a
        // FunctionCall result carries no SsisType of its own -- TranslateCondition can't decide
        // string-vs-numeric comparison semantics from either side.
        var ast = SsisExpression.Parse("UPPER(Name) == UPPER(Other)");
        var refs = Refs(("Name", "row.Name", SsisType.WStr), ("Other", "row.Other", SsisType.WStr));

        var result = ExpressionTranslator.TranslateCondition(ast, refs);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("no operand with a resolvable type", gap.Reason);
    }
}
