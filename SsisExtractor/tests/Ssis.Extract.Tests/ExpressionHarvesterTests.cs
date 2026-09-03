using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;
using Ssis.Extract.Model.Shared;

namespace Ssis.Extract.Tests;

/// <summary>
/// Unit tests for <see cref="ExpressionHarvester"/> (plan §5.5), focused on the tokenizer:
/// SSIS stores a Derived Column's raw expression with functions in <c>[BRACKET]</c> form and
/// cast operators that look like function calls to a naive scan, and getting either wrong
/// would corrupt the "which functions must a replacement support" number this exists to
/// produce.
/// </summary>
public class ExpressionHarvesterTests
{
    private static PackageSpec PackageWithDerivedColumn(string expression, string? friendly)
    {
        var comp = new PipelineComponentSpec
        {
            RefId = "C1", Name = "Der", ComponentClassId = "Microsoft.DerivedColumn",
            Outputs =
            [
                new PipelineOutputSpec
                {
                    RefId = "C1.Out", Name = "Out",
                    Columns =
                    [
                        new PipelineOutputColumnSpec
                        {
                            RefId = "C1.Out.X", Name = "X", LineageId = "C1.Out.X",
                            Expression = expression, FriendlyExpression = friendly,
                        },
                    ],
                },
            ],
        };
        var pipeline = new PipelineSpec { Components = [comp] };
        var basePkg = TestFixtures.MinimalPackage("P");
        return Clone(basePkg, executables:
        [
            new ExecutableSpec
            {
                RefId = "DFT1", ObjectName = "DFT1", ExecutableType = "Microsoft.Pipeline",
                DataFlowTask = new DataFlowTaskPayload { Pipeline = pipeline, Lineage = LineageBuilder.Build(pipeline) },
            },
        ]);
    }

    private static PackageSpec Clone(PackageSpec p, List<ExecutableSpec>? executables = null,
        List<PropertyExpressionSpec>? propertyExpressions = null, List<VariableSpec>? variables = null) => new()
        {
            ObjectName = p.ObjectName,
            SourceDtsxPath = p.SourceDtsxPath,
            Sha256 = p.Sha256,
            FileSizeBytes = p.FileSizeBytes,
            LastWriteTimeUtc = p.LastWriteTimeUtc,
            ProtectionLevelRaw = p.ProtectionLevelRaw,
            ProtectionLevelName = p.ProtectionLevelName,
            Coverage = p.Coverage,
            Executables = executables ?? [],
            PropertyExpressions = propertyExpressions ?? [],
            Variables = variables ?? [],
        };

    [Fact]
    public void CastOperators_AreNotCountedAsFunctions()
    {
        // (DT_WSTR,101) looks exactly like a function call to a naive NAME( scan. Counting
        // it would inflate the "functions a replacement must support" number.
        var package = PackageWithDerivedColumn("(DT_WSTR,101)(a + b)", "(DT_WSTR,101)(a + b)");

        var harvested = Assert.Single(ExpressionHarvester.Harvest(package));

        Assert.Empty(harvested.Functions);
        Assert.Equal(["DT_WSTR"], harvested.Casts);
    }

    [Fact]
    public void BracketedFunctionForm_IsRecognized()
    {
        // How SSIS actually stores a Derived Column's raw expression -- verified against
        // this PoC's own LoadEmployees.dtsx, which contains exactly this shape.
        var package = PackageWithDerivedColumn(
            "(DT_WSTR,20)([UPPER]([SUBSTRING](#{col},1,3)))",
            friendly: null);

        var harvested = Assert.Single(ExpressionHarvester.Harvest(package));

        Assert.Contains("UPPER", harvested.Functions);
        Assert.Contains("SUBSTRING", harvested.Functions);
        Assert.Equal(["DT_WSTR"], harvested.Casts);
        Assert.Equal(["col"], harvested.ReferencedColumns);
    }

    [Fact]
    public void PlainFunctionForm_IsRecognizedToo()
    {
        var package = PackageWithDerivedColumn("GETUTCDATE()", "GETUTCDATE()");

        var harvested = Assert.Single(ExpressionHarvester.Harvest(package));

        Assert.Equal(["GETUTCDATE"], harvested.Functions);
        Assert.Empty(harvested.Casts);
    }

    [Fact]
    public void VariableReferences_AreExtractedFromPropertyExpressions()
    {
        var package = Clone(TestFixtures.MinimalPackage("P"),
            propertyExpressions: [new PropertyExpressionSpec { PropertyName = "ConnectionString", Expression = "@[User::SourceFilePath]" }]);

        var harvested = Assert.Single(ExpressionHarvester.Harvest(package));

        Assert.Equal("PropertyExpression", harvested.Kind);
        Assert.Equal("ConnectionString", harvested.TargetProperty);
        Assert.Equal(["User::SourceFilePath"], harvested.ReferencedVariables);
    }

    [Fact]
    public void NonExpressionVariable_IsNotHarvested()
    {
        // A variable holding a literal value has no expression to harvest -- only
        // EvaluateAsExpression ones do.
        var package = Clone(TestFixtures.MinimalPackage("P"),
            variables: [new VariableSpec { Namespace = "User", ObjectName = "Plain", OwningContainerRefId = "Package", EvaluateAsExpression = false, Expression = null }]);

        Assert.Empty(ExpressionHarvester.Harvest(package));
    }

    [Fact]
    public void EmptyPackage_HarvestsNothing()
    {
        Assert.Empty(ExpressionHarvester.Harvest(TestFixtures.MinimalPackage("P")));
    }
}
