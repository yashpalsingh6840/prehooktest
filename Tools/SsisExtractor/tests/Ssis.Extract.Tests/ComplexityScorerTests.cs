using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Tests;

/// <summary>Unit tests for <see cref="ComplexityScorer.Score"/> against synthetic packages, covering the classification rule order documented on <c>ComplexityScorer.Classify</c>. The two real PoC packages (see <c>GoldenFileTests</c>/<c>ReportCommand</c> output) only exercise the "Simple"/"Procedural" outcomes -- neither has a script task, an unmapped task, or a pure-SQL data flow to reach the other two branches.</summary>
public class ComplexityScorerTests
{
    [Fact]
    public void EmptyPackage_IsSimple()
    {
        var package = TestFixtures.MinimalPackage("Empty");

        var stats = ComplexityScorer.Score(package, ComplexityWeights.Default);

        Assert.Equal(0, stats.TaskCount);
        Assert.Equal(0, stats.Score);
        Assert.Equal("Simple", stats.Classification);
    }

    [Fact]
    public void UnmappedTask_ForcesHighRisk_RegardlessOfScore()
    {
        var unmapped = new ExecutableSpec
        {
            RefId = "X",
            ExecutableType = "Some.UnknownTask",
            UnmappedTask = new UnmappedTaskPayload { RawObjectDataXml = "<x/>" },
        };
        var package = CloneWithExecutables(TestFixtures.MinimalPackage("HasUnmapped"), [unmapped]);

        var stats = ComplexityScorer.Score(package, ComplexityWeights.Default);

        Assert.Equal(1, stats.UnmappedNodeCount);
        Assert.Equal("HighRisk", stats.Classification);
    }

    [Fact]
    public void SqlTasksWithNoDataFlow_IsSqlHeavy()
    {
        var package = TestFixtures.MinimalPackage("PureSql");
        var sqlTask = new ExecutableSpec
        {
            RefId = "SQL1",
            ExecutableType = "Microsoft.ExecuteSQLTask",
            ExecuteSqlTask = new ExecuteSqlTaskPayload(),
        };
        package = CloneWithExecutables(package, [sqlTask]);

        var stats = ComplexityScorer.Score(package, ComplexityWeights.Default);

        Assert.Equal(1, stats.SqlStatementCount);
        Assert.Equal(0, stats.DataFlowTaskCount);
        Assert.Equal("SqlHeavy", stats.Classification);
    }

    [Fact]
    public void ScoreAboveHighRiskThreshold_IsHighRisk_EvenWithoutScriptOrUnmapped()
    {
        var package = TestFixtures.MinimalPackage("BigPackage");
        // 70 plain SQL tasks * weight 2 = 140, comfortably over the default 60 threshold,
        // with nothing else that would independently force a classification.
        var tasks = Enumerable.Range(0, 70)
            .Select(i => new ExecutableSpec { RefId = $"SQL{i}", ExecutableType = "Microsoft.ExecuteSQLTask", ExecuteSqlTask = new ExecuteSqlTaskPayload() })
            .ToList();
        package = CloneWithExecutables(package, tasks);

        var stats = ComplexityScorer.Score(package, ComplexityWeights.Default);

        Assert.True(stats.Score >= ComplexityWeights.Default.HighRiskThreshold);
        Assert.Equal("HighRisk", stats.Classification);
    }

    private static PackageSpec CloneWithExecutables(PackageSpec package, List<ExecutableSpec> executables) => new()
    {
        ObjectName = package.ObjectName,
        SourceDtsxPath = package.SourceDtsxPath,
        Sha256 = package.Sha256,
        FileSizeBytes = package.FileSizeBytes,
        LastWriteTimeUtc = package.LastWriteTimeUtc,
        ProtectionLevelRaw = package.ProtectionLevelRaw,
        ProtectionLevelName = package.ProtectionLevelName,
        Coverage = package.Coverage,
        Executables = executables,
    };
}
