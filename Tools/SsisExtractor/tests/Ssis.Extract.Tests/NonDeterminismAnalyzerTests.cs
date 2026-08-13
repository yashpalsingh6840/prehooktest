using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Pipeline;

namespace Ssis.Extract.Tests;

/// <summary>Unit tests for <see cref="NonDeterminismAnalyzer"/> against synthetic pipelines. The real PoC packages (see <c>ReportCommand</c>'s own output, matching this repo's CLAUDE.md "Sensitive credential"/coverage notes) only exercise the "reaches a destination" case for <c>GETUTCDATE()</c> -- not a literal constant (correctly not flagged), not <c>NEWID()</c>/a system variable, and not the "never reaches a destination" case.</summary>
public class NonDeterminismAnalyzerTests
{
    private static PipelineOutputColumnSpec OutputColumn(string refId, string name, string? expression) => new()
    {
        RefId = refId,
        Name = name,
        LineageId = refId,
        Expression = expression,
        FriendlyExpression = expression,
    };

    [Fact]
    public void LiteralConstant_IsNotFlagged()
    {
        var comp = new PipelineComponentSpec
        {
            RefId = "C1", Name = "Der", ComponentClassId = "Microsoft.DerivedColumn",
            Outputs = [new PipelineOutputSpec { RefId = "C1.Out", Name = "Out", Columns = [OutputColumn("C1.Out.X", "X", "\"a literal\"")] }],
        };
        var pipeline = new PipelineSpec { Components = [comp] };
        var package = PackageWithOneDft(pipeline);

        var results = NonDeterminismAnalyzer.Analyze(package, PackageTree.AllExecutables(package).ToList());

        Assert.Empty(results);
    }

    [Fact]
    public void NewId_IsFlaggedWithCorrectReason()
    {
        var comp = new PipelineComponentSpec
        {
            RefId = "C1", Name = "Der", ComponentClassId = "Microsoft.DerivedColumn",
            Outputs = [new PipelineOutputSpec { RefId = "C1.Out", Name = "Out", Columns = [OutputColumn("C1.Out.Id", "Id", "NEWID()")] }],
        };
        var pipeline = new PipelineSpec { Components = [comp] };
        var package = PackageWithOneDft(pipeline);

        var results = NonDeterminismAnalyzer.Analyze(package, PackageTree.AllExecutables(package).ToList());

        var result = Assert.Single(results);
        Assert.Equal("NEWID", result.Reason);
        Assert.Null(result.TargetTable); // never traced to a destination in this fixture
    }

    [Fact]
    public void NonDeterministicColumn_TracedThroughPassthroughToDestination()
    {
        var der = new PipelineComponentSpec
        {
            RefId = "Der", Name = "Der", ComponentClassId = "Microsoft.DerivedColumn",
            Outputs = [new PipelineOutputSpec { RefId = "Der.Out", Name = "Out", Columns = [OutputColumn("Der.Out.Ts", "Ts", "GETUTCDATE()")] }],
        };
        var dst = new PipelineComponentSpec
        {
            RefId = "Dst", Name = "Dst", ComponentClassId = "Microsoft.OLEDBDestination",
            Inputs = [new PipelineInputSpec { RefId = "Dst.In", Name = "In", Columns = [new PipelineInputColumnSpec { RefId = "Dst.In.Ts", CachedName = "Ts", LineageId = "Der.Out.Ts" }] }],
            OleDbDestination = new OleDbDestinationPayload { OpenRowset = "[dbo].[Target]", ColumnMappings = [new PipelineColumnMappingSpec { ComponentColumnName = "Ts", ExternalColumnName = "TsCol" }] },
        };
        var pipeline = new PipelineSpec { Components = [der, dst] };
        var package = PackageWithOneDft(pipeline);

        var results = NonDeterminismAnalyzer.Analyze(package, PackageTree.AllExecutables(package).ToList());

        var result = Assert.Single(results);
        Assert.Equal("GETUTCDATE", result.Reason);
        Assert.Equal("[dbo].[Target]", result.TargetTable);
        Assert.Equal("TsCol", result.TargetColumn);
        Assert.Equal("Dst", result.LandingComponentName);
    }

    private static PackageSpec PackageWithOneDft(PipelineSpec pipeline)
    {
        var package = TestFixtures.MinimalPackage("P");
        var dft = new ExecutableSpec
        {
            RefId = "DFT1",
            ObjectName = "DFT1",
            ExecutableType = "Microsoft.Pipeline",
            DataFlowTask = new DataFlowTaskPayload { Pipeline = pipeline, Lineage = LineageBuilder.Build(pipeline) },
        };
        return new PackageSpec
        {
            ObjectName = package.ObjectName,
            SourceDtsxPath = package.SourceDtsxPath,
            Sha256 = package.Sha256,
            FileSizeBytes = package.FileSizeBytes,
            LastWriteTimeUtc = package.LastWriteTimeUtc,
            ProtectionLevelRaw = package.ProtectionLevelRaw,
            ProtectionLevelName = package.ProtectionLevelName,
            Coverage = package.Coverage,
            Executables = [dft],
        };
    }
}
