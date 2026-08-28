using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Codegen.Tests;

public class NullabilityInferenceTests
{
    private static PipelineComponentSpec DerivedColumnWithExpression(string columnName, string expression) => new()
    {
        RefId = "DER_Test",
        Name = "DER_Test",
        ComponentClassId = "Microsoft.DerivedColumn",
        Outputs =
        [
            new PipelineOutputSpec
            {
                RefId = "DER_Test.Outputs[Derived Column Output]",
                Name = "Derived Column Output",
                Columns =
                [
                    new PipelineOutputColumnSpec
                    {
                        RefId = $"DER_Test.Outputs[Derived Column Output].Columns[{columnName}]",
                        Name = columnName,
                        LineageId = $"DER_Test.Outputs[Derived Column Output].Columns[{columnName}]",
                        Expression = expression,
                        FriendlyExpression = expression,
                    },
                ],
            },
        ],
    };

    [Fact]
    public void InferNullableColumnNames_FindsAReferenceInsideATernaryCondition()
    {
        // The exact real-case shape: ISNULL(x) as a ternary's own condition, not the whole
        // expression -- RBC_Demo_ETL's Package_Transforms.dtsx, DER_Enrich.TenureDays.
        var derivedColumn = DerivedColumnWithExpression("TenureDays", "ISNULL(SignupDate) ? -1 : DATEDIFF(\"dd\",SignupDate,GETDATE())");

        var result = NullabilityInference.InferNullableColumnNames([derivedColumn]);

        Assert.Contains("SignupDate", result);
        Assert.Single(result);
    }

    [Fact]
    public void InferNullableColumnNames_FindsAReferenceInsideALogicalAndCondition()
    {
        // RBC_Demo_ETL's own CSPLIT_Validity shape (a Conditional Split condition, not a
        // Derived Column -- included here only to prove the AST walk itself reaches inside
        // &&, not to claim RouterEmitter consumes this; it deliberately doesn't yet).
        var derivedColumn = DerivedColumnWithExpression("IsValid", "!ISNULL(CustomerID) && LEN(Email) > 0");

        var result = NullabilityInference.InferNullableColumnNames([derivedColumn]);

        Assert.Contains("CustomerID", result);
    }

    [Fact]
    public void InferNullableColumnNames_ReturnsEmpty_WhenNoExpressionUsesIsnull()
    {
        var derivedColumn = DerivedColumnWithExpression("FullName", "TRIM(FirstName) + \" \" + TRIM(LastName)");

        var result = NullabilityInference.InferNullableColumnNames([derivedColumn]);

        Assert.Empty(result);
    }

    [Fact]
    public void InferNullableColumnNames_DoesNotFlagAFunctionCallArgument_OnlyABareReference()
    {
        // ISNULL's argument must be a bare Reference to count as evidence a COLUMN can be null
        // -- ISNULL(SomeFunction(x)) doesn't say anything about x's own nullability.
        var derivedColumn = DerivedColumnWithExpression("X", "ISNULL(UPPER(Name)) ? \"a\" : \"b\"");

        var result = NullabilityInference.InferNullableColumnNames([derivedColumn]);

        Assert.Empty(result);
    }
}
