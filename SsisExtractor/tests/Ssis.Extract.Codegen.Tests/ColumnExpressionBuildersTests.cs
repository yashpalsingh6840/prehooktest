namespace Ssis.Extract.Codegen.Tests;

/// <summary>Unit tests for the three small expression builders extracted out of
/// TransformEmitter's own per-column cascade (2026-09-03) -- each is a pure function with an
/// explicit input and output, independently testable without constructing a whole
/// PipelineComponentSpec/TransformRequest the way TransformEmitterTests needs to.</summary>
public class ColumnExpressionBuildersTests
{
    [Fact]
    public void LookupJoinExpressionBuilder_BuildsTheIndexerExpression()
    {
        var join = new LookupJoinSpec(
            CacheClassName: "LKP_CountryCache",
            CacheParameterName: "lookup",
            InputColumnName: "CountryId",
            KeyClrTypeName: "int",
            OutputToReferenceColumn: new Dictionary<string, string> { ["Region"] = "RegionName" });

        var result = LookupJoinExpressionBuilder.Build(join, "RegionName");

        Assert.Equal("lookup[row.CountryId].RegionName", result);
    }

    [Theory]
    [InlineData(NumericCoercionKind.NarrowR8ToI4, "SsisFn.NarrowR8ToI4(row.CustomerID)")]
    [InlineData(NumericCoercionKind.NarrowI8ToI4, "SsisFn.NarrowI8ToI4(row.CustomerID)")]
    [InlineData(NumericCoercionKind.NarrowNumericToI4, "SsisFn.NarrowNumericToI4(row.CustomerID)")]
    [InlineData(NumericCoercionKind.NarrowR4ToI4, "SsisFn.NarrowR4ToI4(row.CustomerID)")]
    [InlineData(NumericCoercionKind.ParseWstrToI4, "SsisFn.ParseWstrToI4(row.CustomerID)")]
    public void NumericCoercionExpressionBuilder_BuildsTheCallForEachEvidencedPairing(NumericCoercionKind kind, string expected)
    {
        var result = NumericCoercionExpressionBuilder.Build(kind, "CustomerID");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FlatFileStringConversionExpressionBuilder_UsesAPlainToString_WhenNotNullable()
    {
        var result = FlatFileStringConversionExpressionBuilder.Build("ID", isNullable: false);

        Assert.Equal("row.ID.ToString()", result);
    }

    [Fact]
    public void FlatFileStringConversionExpressionBuilder_UsesANullConditionalToString_WhenNullable()
    {
        var result = FlatFileStringConversionExpressionBuilder.Build("ID", isNullable: true);

        Assert.Equal("row.ID?.ToString() ?? \"\"", result);
    }
}
