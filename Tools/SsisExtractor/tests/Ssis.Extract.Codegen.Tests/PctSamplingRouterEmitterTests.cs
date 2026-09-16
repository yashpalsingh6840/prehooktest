namespace Ssis.Extract.Codegen.Tests;

/// <summary>Phase 4 of the unsupported-component-types plan. Unlike RouterEmitterTests
/// (Conditional Split), this emitter needs no fixture/PackagePlanner round-trip at all --
/// it's a pure function of (namespace, class name, row type, SamplingValue, SamplingSeed),
/// so these tests exercise it directly.</summary>
public class PctSamplingRouterEmitterTests
{
    [Fact]
    public void Emit_ProducesARouterClass_SeededFromSamplingSeed_ComparingAgainstSamplingValue()
    {
        var result = PctSamplingRouterEmitter.Emit("Pkg.Mapping", "MySamplingRouter", "Pkg.Sql", "MyRow", samplingValue: 25, samplingSeed: 777);

        var file = Assert.Single(result.Files);
        Assert.Equal("Mapping/MySamplingRouter.cs", file.RelativePath);
        Assert.Contains("public sealed class MySamplingRouter : IRowRouter<MyRow>", file.Content);
        Assert.Contains("private readonly Random _random = new(777);", file.Content);
        Assert.Contains("public int SelectBranch(MyRow row, in RowContext ctx)", file.Content);
        Assert.Contains("return _random.Next(100) < 25 ? 0 : 1;", file.Content);
        Assert.Contains("using Pkg.Sql;", file.Content);
        Assert.Contains("using Etl.Core.Abstractions;", file.Content);
        Assert.Contains("namespace Pkg.Mapping;", file.Content);
        Assert.Empty(result.Gaps);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_AcceptsASamplingSeedOfZero_TheSchemaDefault()
    {
        // Confirmed via a live object-model probe (Ssis.Extract.FixtureBuilder's own
        // ProbePctSampling): SamplingSeed's own schema default is 0, an ordinary, valid .NET
        // Random seed -- no special-casing needed, unlike some other raw-enum/default-value
        // resolutions elsewhere in this tool.
        var result = PctSamplingRouterEmitter.Emit("Pkg.Mapping", "Router", "Pkg.Sql", "Row", samplingValue: 10, samplingSeed: 0);

        var file = Assert.Single(result.Files);
        Assert.Contains("new(0);", file.Content);
        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
