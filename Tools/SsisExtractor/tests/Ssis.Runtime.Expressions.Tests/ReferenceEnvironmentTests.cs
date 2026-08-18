using Ssis.Runtime.Expressions;

namespace Ssis.Runtime.Expressions.Tests;

/// <summary>
/// The oracle corpus is entirely self-contained literals -- it can't exercise identifier
/// resolution (there's no SSIS package variable/column the probe could bind generically).
/// But that's exactly what <c>ssisx testgen</c> needs: a Derived Column's
/// <c>FriendlyExpression</c> (e.g. <c>(DT_WSTR,101)(FirstName + " " + LastName)</c>) is
/// evaluated with real column values bound as bare-identifier references. This suite covers
/// that path directly, unverified by the corpus itself.
/// </summary>
public class ReferenceEnvironmentTests
{
    [Fact]
    public void ResolvesBareIdentifiersFromEnvironment()
    {
        var env = new Dictionary<string, SsisValue>
        {
            ["FirstName"] = SsisValue.OfString("Jane"),
            ["LastName"] = SsisValue.OfString("Doe"),
        };

        var result = SsisExpression.Evaluate("(DT_WSTR,101)(FirstName + \" \" + LastName)", env);

        Assert.False(result.IsNull);
        Assert.Equal("Jane Doe", result.ToDisplayString());
    }

    [Fact]
    public void ResolvesAtBracketReferencesByNamespaceQualifiedName()
    {
        var env = new Dictionary<string, SsisValue>
        {
            ["User::SourceFolder"] = SsisValue.OfString("D:/Data"),
        };

        var result = SsisExpression.Evaluate("@[User::SourceFolder] + \"/Employees.csv\"", env);

        Assert.Equal("D:/Data/Employees.csv", result.ToDisplayString());
    }

    [Fact]
    public void UnresolvedReferenceThrows()
    {
        var ex = Assert.Throws<SsisExpressionError>(() => SsisExpression.Evaluate("MissingColumn + \"x\""));
        Assert.Contains("MissingColumn", ex.Message);
    }

    [Fact]
    public void NullTypedColumnValuePropagatesThroughAnExpressionJustLikeANullLiteral()
    {
        var env = new Dictionary<string, SsisValue>
        {
            ["Department"] = SsisValue.Null(SsisType.WStr),
        };

        var result = SsisExpression.Evaluate("UPPER(SUBSTRING(Department,1,3))", env);

        Assert.True(result.IsNull);
    }

    [Fact]
    public void RealDerivedColumnShapeFromThisPoCsLoadEmployeesPackage()
    {
        // The actual EmployeeKey expression from SSIS/LoadEmployees.dtsx's Derived Column
        // (per Migration-Validation-Plan.md §4's own worked example), with column values
        // standing in for what the pipeline would have bound.
        var env = new Dictionary<string, SsisValue>
        {
            ["Department"] = SsisValue.OfString("Engineering"),
            ["EmployeeID"] = SsisValue.OfInt(42),
        };

        var result = SsisExpression.Evaluate(
            "(DT_WSTR,20)(UPPER(SUBSTRING(Department,1,3)) + \"-\" + (DT_WSTR,10)EmployeeID)", env);

        Assert.Equal("ENG-42", result.ToDisplayString());
    }
}
