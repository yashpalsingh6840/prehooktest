using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen.Tests;

public class ExpressionTaskEmitterTests
{
    private static Dictionary<string, ColumnReference> Vars(
        params (string Name, string CSharpExpression, SsisType Type)[] refs) =>
        refs.ToDictionary(r => r.Name, r => new ColumnReference(r.CSharpExpression, r.Type));

    [Fact]
    public void TranslateAssignment_TranslatesTheRealDateAddCutoffExpression()
    {
        // DailyETLMain.dtsx's own "Calculate ETL Cutoff Time backup" task, verbatim -- the real
        // evidenced Microsoft.ExpressionTask call this whole feature was built for.
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::TargetETLCutoffTime] = DATEADD(\"Minute\", -5, GETUTCDATE()  )", Vars(), out var variableName);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("User::TargetETLCutoffTime", variableName);
        Assert.Equal("DateTime.UtcNow.AddMinutes(-(5))", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateAssignment_TranslatesTheRealTrimMillisecondsExpression()
    {
        // sql-server-samples' DailyETLMain.dtsx's own "Trim Any Milliseconds" task, verbatim --
        // Phase 6's own real evidenced call, the composed DATEADD/DATEPART/'-' idiom that zeroes
        // a DateTime variable's own millisecond component by subtracting its own reported value.
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::TargetETLCutoffTime] = DATEADD(\"Millisecond\", 0 - DATEPART(\"Millisecond\", @[User::TargetETLCutoffTime]), @[User::TargetETLCutoffTime])",
            Vars(("User::TargetETLCutoffTime", "packageVariables.GetRequired<DateTime>(\"User::TargetETLCutoffTime\")", SsisType.DbTimeStamp)),
            out var variableName);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("User::TargetETLCutoffTime", variableName);
        Assert.Equal(
            "SsisFn.DateAddMillisecond(packageVariables.GetRequired<DateTime>(\"User::TargetETLCutoffTime\"), " +
            "(0) - (SsisFn.DatePartMillisecond(packageVariables.GetRequired<DateTime>(\"User::TargetETLCutoffTime\"))))",
            ok.CSharpExpression);
    }

    [Fact]
    public void TranslateAssignment_DegradesToAGap_ForAnUnsupportedDatePart_OnDatePart()
    {
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::X] = DATEPART(\"Quarter\", GETUTCDATE())", Vars(), out _);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("Millisecond", gap.Reason);
    }

    [Fact]
    public void TranslateAssignment_DegradesToAGap_ForStringSubtraction_NotJustSilentlyGuessingItsFine()
    {
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::Y] = \"a\" - \"b\"", Vars(), out _);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("numeric operands", gap.Reason);
    }

    [Fact]
    public void TranslateAssignment_TranslatesAPlainStringLiteralAssignment()
    {
        // The real shape of every OTHER ExpressionTask in DailyETLMain.dtsx, e.g.
        // "Set TableName to City".
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::TableName] = \"City\"", Vars(), out var variableName);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("User::TableName", variableName);
        Assert.Equal("\"City\"", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateAssignment_TranslatesGetDate_AsDateTimeNow_NotUtcNow()
    {
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::LocalStamp] = GETDATE()", Vars(), out _);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("DateTime.Now", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateAssignment_ResolvesAReferenceToAnotherPackageVariable()
    {
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::Copy] = @[User::Source]",
            Vars(("User::Source", "packageVariables.GetRequired<string>(\"User::Source\")", SsisType.WStr)),
            out var variableName);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("User::Copy", variableName);
        Assert.Equal("packageVariables.GetRequired<string>(\"User::Source\")", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateAssignment_ReportsAGap_ForAReferenceWithNoResolvableDeclaredType()
    {
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::Copy] = @[User::Unresolvable]", Vars(), out _);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("User::Unresolvable", gap.Reason);
    }

    [Fact]
    public void TranslateAssignment_ReportsAGap_WhenThereIsNoTopLevelAssignment()
    {
        var result = ExpressionTaskEmitter.TranslateAssignment("\"just a string\"", Vars(), out var variableName);

        Assert.IsType<NotTranslatable>(result);
        Assert.Null(variableName);
    }

    [Fact]
    public void TranslateAssignment_ReportsAGap_WhenTheLeftHandSideIsNotABareReference()
    {
        var result = ExpressionTaskEmitter.TranslateAssignment("1 + 1 = 2", Vars(), out _);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("not a bare variable reference", gap.Reason);
    }

    [Fact]
    public void TranslateAssignment_ReportsAGap_ForAnUnsupportedRhsShape()
    {
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::X] = UPPER(\"a\")", Vars(), out _);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("unsupported expression shape", gap.Reason);
    }

    [Fact]
    public void TranslateAssignment_ReportsAGap_ForADateAddPartThatIsNotOracleVerified()
    {
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::X] = DATEADD(\"Quarter\", 1, GETUTCDATE())", Vars(), out _);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("Minute", gap.Reason);
    }

    // --- Numeric '+' (Phase 3, STOCK:FORLOOP's own AssignExpression) ---

    [Fact]
    public void TranslateAssignment_TranslatesNumericAddition_ForALoopCounterIncrement()
    {
        // UseCase_34's own "For Loop Container" AssignExpression, verbatim (once ForLoopEmitter's
        // own bare-@ rewrite has already run -- this translator itself only ever sees @[...]).
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::Part] = @[User::Part] + 1",
            Vars(("User::Part", "packageVariables.GetRequired<int>(\"User::Part\")", SsisType.I4)),
            out var variableName);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("User::Part", variableName);
        Assert.Equal("(packageVariables.GetRequired<int>(\"User::Part\")) + (1)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateAssignment_TranslatesNumericAddition_WhenTheReferenceIsOnTheRight()
    {
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::Y] = 1 + @[User::X]",
            Vars(("User::X", "packageVariables.GetRequired<int>(\"User::X\")", SsisType.I4)),
            out _);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("(1) + (packageVariables.GetRequired<int>(\"User::X\"))", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateAssignment_ReportsAGap_ForStringConcatenation_NotJustSilentlyGuessingItsFine()
    {
        // Deliberately NOT supported by this translator -- unevidenced for a control-flow-level
        // assignment (unlike ExpressionTranslator's own pipeline-row-context Derived Column
        // translation, which DOES support string '+').
        var result = ExpressionTaskEmitter.TranslateAssignment(
            "@[User::Y] = \"a\" + \"b\"", Vars(), out _);

        var gap = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("numeric operands", gap.Reason);
    }

    // --- SplitTopLevelAssignment: the naive IndexOf('=') traps this has to get right ---

    [Theory]
    [InlineData("@[User::X] = 1", "@[User::X]", "1")]
    [InlineData("@[User::X]=1", "@[User::X]", "1")]
    [InlineData("@[User::X] = \"a=b\"", "@[User::X]", "\"a=b\"")] // '=' inside a string literal
    [InlineData("@[User::X] = 1 == 1", "@[User::X]", "1 == 1")] // "==" is not the assignment
    [InlineData("@[User::X] = 1 != 2", "@[User::X]", "1 != 2")] // "!=" is not the assignment
    [InlineData("@[User::X] = 1 <= 2", "@[User::X]", "1 <= 2")] // "<=" is not the assignment
    [InlineData("@[User::X] = 1 >= 2", "@[User::X]", "1 >= 2")] // ">=" is not the assignment
    public void SplitTopLevelAssignment_FindsTheRealAssignment_NotAComparisonOperator(
        string expr, string expectedLhs, string expectedRhs)
    {
        var split = ExpressionTaskEmitter.SplitTopLevelAssignment(expr);

        Assert.NotNull(split);
        Assert.Equal(expectedLhs, split.Value.Lhs);
        Assert.Equal(expectedRhs, split.Value.Rhs);
    }

    [Fact]
    public void SplitTopLevelAssignment_ReturnsNull_WhenThereIsNoBareEquals()
    {
        Assert.Null(ExpressionTaskEmitter.SplitTopLevelAssignment("1 == 1"));
    }
}
