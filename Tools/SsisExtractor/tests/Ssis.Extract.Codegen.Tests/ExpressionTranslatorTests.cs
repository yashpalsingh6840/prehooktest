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
    public void TranslateColumn_TranslatesTrim_AsAValueProducingFunction()
    {
        var ast = SsisExpression.Parse("TRIM(Email)");
        var refs = Refs(("Email", "row.Email", SsisType.WStr));

        var result = ExpressionTranslator.TranslateColumn(ast, "Customer", "TrimmedEmail", refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("SsisFn.Trim(row.Email)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateCondition_TranslatesFindStringNestedInsideTrim_TheExactRealCaseShape()
    {
        // RBC_Demo_ETL's own Package_Transforms.dtsx, CSPLIT_Validity's "Valid" case (minus the
        // ISNULL/CustomerID_i4 half, already proven working last session):
        // FINDSTRING(TRIM(Email),"@",1) > 0
        var ast = SsisExpression.Parse("FINDSTRING(TRIM(Email),\"@\",1) > 0");
        var refs = Refs(("Email", "row.Email", SsisType.WStr));

        var result = ExpressionTranslator.TranslateCondition(ast, refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("(SsisFn.FindString(SsisFn.Trim(row.Email), \"@\", 1) > 0)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateColumn_TranslatesTernary_OfStringLiterals()
    {
        // The real SyntheticTernary.dtsx case: Amount > 1000 ? "High" : "Low".
        var ast = SsisExpression.Parse("Amount > 1000 ? \"High\" : \"Low\"");
        var refs = Refs(("Amount", "row.Amount", SsisType.Numeric));

        var result = ExpressionTranslator.TranslateColumn(ast, "Order", "Category", refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("((row.Amount > 1000) ? \"High\" : \"Low\")", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateColumn_TranslatesTernaryWithNegate_TheExactRealCaseShape()
    {
        // RBC_Demo_ETL's own Package_Transforms.dtsx, DER_Enrich.TenureDays uses this exact
        // shape (ISNULL(...) ? -1 : DATEDIFF(...)) -- DATEDIFF itself is a separate, still-open
        // gap (never oracle-verified), so this test isolates just the ternary/negate half with
        // an already-supported whenFalse branch.
        var ast = SsisExpression.Parse("Amount < 0 ? -1 : 1");
        var refs = Refs(("Amount", "row.Amount", SsisType.Numeric));

        var result = ExpressionTranslator.TranslateColumn(ast, "Order", "SignFlag", refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("((row.Amount < 0) ? -(1) : 1)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateColumn_ReportsAGap_WhenATernaryBranchIsUnsupported()
    {
        // DATEDIFF is real SSIS syntax and the runtime evaluator does NOT support it either
        // (never oracle-verified) -- confirms the ternary degrades per-branch rather than
        // guessing, and confirms the whenFalse branch is what's actually checked here.
        var ast = SsisExpression.Parse("ISNULL(SignupDate) ? -1 : DATEDIFF(\"dd\",SignupDate,GETDATE())");
        var refs = Refs(("SignupDate", "row.SignupDate", SsisType.DbDate));

        var result = ExpressionTranslator.TranslateColumn(ast, "Order", "TenureDays", refs);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("FunctionCall", gap.Reason);
    }

    [Fact]
    public void TranslateCondition_TranslatesStringEquality_WhenBothOperandsAreKnownFunctionCalls()
    {
        // Superseded by TryResolveOperandType now knowing UPPER's own return type (added
        // alongside numeric '+' dispatch, 2026-08-27) -- both operands translate fine as VALUES
        // and the comparison itself now resolves as string-vs-numeric via that same lookup,
        // where before neither side had ANY resolvable type. A genuine capability increase, not
        // just a side effect: this exact shape was NotTranslatable before this change.
        var ast = SsisExpression.Parse("UPPER(Name) == UPPER(Other)");
        var refs = Refs(("Name", "row.Name", SsisType.WStr), ("Other", "row.Other", SsisType.WStr));

        var result = ExpressionTranslator.TranslateCondition(ast, refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal(
            "string.Equals(SsisFn.Upper(row.Name), SsisFn.Upper(row.Other), System.StringComparison.Ordinal)",
            ok.CSharpExpression);
    }

    [Fact]
    public void TranslateCondition_ReportsAGap_WhenNeitherOperandHasAResolvableType()
    {
        // A nested (DT_WSTR,n) int-to-string cast translates fine as a VALUE
        // (TranslateIntToStringCast), but TryResolveOperandType has no Cast case -- genuinely
        // still unresolvable on either side, unlike UPPER/TRIM/SUBSTRING/FINDSTRING above, all
        // of which now resolve via KnownFunctionReturnTypes.
        var ast = SsisExpression.Parse("(DT_WSTR,10)A == (DT_WSTR,10)B");
        var refs = Refs(("A", "row.A", SsisType.I4), ("B", "row.B", SsisType.I4));

        var result = ExpressionTranslator.TranslateCondition(ast, refs);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("no operand with a resolvable type", gap.Reason);
    }

    [Fact]
    public void TranslateColumn_TranslatesNumericAddition_NotStringConcat_ForANonStringOperand()
    {
        // The real motivating case: SUBSTRING's own start argument,
        // FINDSTRING(TRIM(Email),"@",1) + 1 (RBC_Demo_ETL's Package_Transforms,
        // DER_Enrich.EmailDomain) -- FINDSTRING returns I4, so '+' must be numeric addition, not
        // string concatenation (which would have produced the wrong, nonsensical
        // $"{SsisFn.FindString(...)}1" before this fix).
        var ast = SsisExpression.Parse("FINDSTRING(TRIM(Email),\"@\",1) + 1");
        var refs = Refs(("Email", "row.Email", SsisType.WStr));

        var result = ExpressionTranslator.TranslateColumn(ast, "Order", "X", refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("(SsisFn.FindString(SsisFn.Trim(row.Email), \"@\", 1) + 1)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateColumn_TranslatesSubstringWithAComputedStartArgument_TheExactRealCaseShape()
    {
        // RBC_Demo_ETL's own Package_Transforms.dtsx, DER_Enrich.EmailDomain's whenTrue branch:
        // SUBSTRING(TRIM(Email), FINDSTRING(TRIM(Email),"@",1) + 1, 100) -- start is no longer
        // required to be a literal IntLiteral.
        var ast = SsisExpression.Parse("SUBSTRING(TRIM(Email),FINDSTRING(TRIM(Email),\"@\",1) + 1,100)");
        var refs = Refs(("Email", "row.Email", SsisType.WStr));

        var result = ExpressionTranslator.TranslateColumn(ast, "Order", "EmailDomain", refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal(
            "SsisFn.Substring(SsisFn.Trim(row.Email), (SsisFn.FindString(SsisFn.Trim(row.Email), \"@\", 1) + 1), 100)",
            ok.CSharpExpression);
    }

    [Fact]
    public void TranslateColumn_TranslatesSubstringWithLiteralArgs_IdenticallyToBefore()
    {
        // Regression guard: the previously-only-supported shape (both start/length literal)
        // must render byte-identical text after generalizing to accept computed args -- proves
        // no existing golden output moved.
        var ast = SsisExpression.Parse("UPPER(SUBSTRING(Department,1,3))");
        var refs = Refs(("Department", "row.Department", SsisType.WStr));

        var result = ExpressionTranslator.TranslateColumn(ast, "Employee", "X", refs);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("SsisFn.Upper(SsisFn.Substring(row.Department, 1, 3))", ok.CSharpExpression);
    }
}
