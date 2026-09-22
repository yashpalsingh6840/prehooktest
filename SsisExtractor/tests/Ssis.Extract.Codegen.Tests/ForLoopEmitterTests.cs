using Ssis.Runtime.Expressions;

namespace Ssis.Extract.Codegen.Tests;

public class ForLoopEmitterTests
{
    private static Dictionary<string, ColumnReference> Vars(
        params (string Name, string CSharpExpression, SsisType Type)[] refs) =>
        refs.ToDictionary(r => r.Name, r => new ColumnReference(r.CSharpExpression, r.Type));

    [Theory]
    [InlineData("@Part = 1", "@[User::Part] = 1")]
    [InlineData("@Part <11", "@[User::Part] <11")]
    [InlineData("@Part = @Part + 1", "@[User::Part] = @[User::Part] + 1")]
    [InlineData("@[User::Already] = 1", "@[User::Already] = 1")] // already-bracketed form is untouched
    public void RewriteBareVariableReferences_RewritesBareAtNameToTheStandardForm(string input, string expected)
    {
        Assert.Equal(expected, ForLoopEmitter.RewriteBareVariableReferences(input));
    }

    [Fact]
    public void TranslateAssignment_TranslatesTheRealInitExpression()
    {
        // UseCase_34's own "For Loop Container", verbatim: DTS:InitExpression="@Part =1".
        var result = ForLoopEmitter.TranslateAssignment(
            "@Part =1", Vars(("User::Part", "packageVariables.GetRequired<int>(\"User::Part\")", SsisType.I4)),
            out var variableName);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("User::Part", variableName);
        Assert.Equal("1", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateAssignment_TranslatesTheRealAssignExpression_AsNumericAddition()
    {
        // UseCase_34's own "For Loop Container", verbatim: DTS:AssignExpression="@Part = @Part + 1".
        var result = ForLoopEmitter.TranslateAssignment(
            "@Part = @Part + 1", Vars(("User::Part", "packageVariables.GetRequired<int>(\"User::Part\")", SsisType.I4)),
            out var variableName);

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("User::Part", variableName);
        Assert.Equal(
            "(packageVariables.GetRequired<int>(\"User::Part\")) + (1)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateCondition_TranslatesTheRealEvalExpression()
    {
        // UseCase_34's own "For Loop Container", verbatim: DTS:EvalExpression="@Part &lt;11" (the
        // saved XML's own decoded form is "@Part <11", no space before the literal).
        var result = ForLoopEmitter.TranslateCondition(
            "@Part <11", Vars(("User::Part", "packageVariables.GetRequired<int>(\"User::Part\")", SsisType.I4)));

        var ok = Assert.IsType<TranslatedOk>(result);
        Assert.Equal("(packageVariables.GetRequired<int>(\"User::Part\") < 11)", ok.CSharpExpression);
    }

    [Fact]
    public void TranslateCondition_ReportsAGap_WhenTheReferencedVariableHasNoResolvableType()
    {
        var result = ForLoopEmitter.TranslateCondition("@Part < 4", Vars());

        var notTranslatable = Assert.IsType<NotTranslatable>(result);
        Assert.Contains("User::Part", notTranslatable.Reason);
    }

    [Fact]
    public void TranslateCondition_ReportsAGap_ForAnUnparsableExpression()
    {
        var result = ForLoopEmitter.TranslateCondition("@Part <", Vars());

        Assert.IsType<NotTranslatable>(result);
    }
}
