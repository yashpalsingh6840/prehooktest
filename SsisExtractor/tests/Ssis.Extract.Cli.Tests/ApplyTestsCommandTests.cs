using System.Text.Json;
using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Cli.Tests;

/// <summary>
/// Covers <c>ApplyTestsCommand</c> -- see <c>Docs/AI-Test-Enrichment-Plan.md</c>. Unlike
/// <c>ApplyFillsCommand</c>, this command needs no <c>gaps.json</c> at all: a "more test" answers
/// no gap, so every file present under <c>MoreTests/</c> is applied unconditionally, and there is
/// no staleness concept to exercise.
///
/// Built as real filesystem integration tests, same reasoning as <see cref="ApplyFillsCommandTests"/>
/// -- this command's whole job is file I/O, so faking it away would test less than doing it.
/// </summary>
public class ApplyTestsCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ssisx-apply-tests-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Package = "Pkg";

    private void WriteGeneratedPackage(bool withTestProject = true)
    {
        Directory.CreateDirectory(Path.Combine(_root, "generate", Package));
        if (withTestProject) Directory.CreateDirectory(Path.Combine(_root, "generate", $"{Package}.Tests"));
    }

    private string WriteMoreTest(string fileName, string content)
    {
        var dir = Path.Combine(_root, "fills", Package, "MoreTests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private List<MoreTestRecordSpec> ReadManifest() =>
        JsonSerializer.Deserialize<List<MoreTestRecordSpec>>(File.ReadAllText(Path.Combine(_root, "tests-applied.json")))!;

    [Fact]
    public void Run_CopiesAnAttributedFile_AndRecordsItsProvenance()
    {
        WriteGeneratedPackage();
        WriteMoreTest("FooMoreTests.cs",
            "// ssisx-more-test: Author=me Date=2026-09-06 Targets=Foo.Bar\nclass X {}\n");

        var exitCode = ApplyTestsCommand.Run(["--out", _root]);

        Assert.Equal(0, exitCode);
        var copied = Path.Combine(_root, "generate", $"{Package}.Tests", "MoreTests", "FooMoreTests.cs");
        Assert.True(File.Exists(copied));

        var manifest = ReadManifest();
        var record = Assert.Single(manifest);
        Assert.Equal(MoreTestStatus.Applied, record.Status);
        Assert.True(record.Attributed);
        Assert.Equal("me", record.Author);
        Assert.Equal("2026-09-06", record.Date);
        Assert.Equal("Foo.Bar", record.Targets);
    }

    [Fact]
    public void Run_CopiesAnUnattributedFile_ButFlagsItAsUndocumented()
    {
        WriteGeneratedPackage();
        WriteMoreTest("BareMoreTests.cs", "class X {}\n");

        var exitCode = ApplyTestsCommand.Run(["--out", _root]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(_root, "generate", $"{Package}.Tests", "MoreTests", "BareMoreTests.cs")));
        var record = Assert.Single(ReadManifest());
        Assert.Equal(MoreTestStatus.Applied, record.Status);
        Assert.False(record.Attributed);
        Assert.Null(record.Author);
    }

    [Fact]
    public void Run_NeverTouchesGapsJson_OrAnyOtherGenerateOutput()
    {
        WriteGeneratedPackage();
        var gapsPath = Path.Combine(_root, "gaps.json");
        File.WriteAllText(gapsPath, "[{\"sentinel\":true}]");
        var reportPath = Path.Combine(_root, "generate-report.md");
        File.WriteAllText(reportPath, "sentinel report");
        WriteMoreTest("FooMoreTests.cs", "class X {}\n");

        ApplyTestsCommand.Run(["--out", _root]);

        Assert.Equal("[{\"sentinel\":true}]", File.ReadAllText(gapsPath));
        Assert.Equal("sentinel report", File.ReadAllText(reportPath));
    }

    [Fact]
    public void Run_ReportsAnUnknownPackage_AndDoesNotCopyIt_ExitCode1()
    {
        // No generate/Pkg directory at all -- this package was never generated into --out.
        WriteMoreTest("FooMoreTests.cs", "class X {}\n");

        var exitCode = ApplyTestsCommand.Run(["--out", _root]);

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "generate", $"{Package}.Tests", "MoreTests")));
        Assert.Empty(ReadManifest());
    }

    [Fact]
    public void Run_ReportsAPackageWithNoTestProject_AndDoesNotCopyIt()
    {
        // generate/Pkg exists but generate/Pkg.Tests does not -- e.g. generated with --skip-tests.
        WriteGeneratedPackage(withTestProject: false);
        WriteMoreTest("FooMoreTests.cs", "class X {}\n");

        var exitCode = ApplyTestsCommand.Run(["--out", _root]);

        Assert.Equal(0, exitCode); // not an "unknown package" refusal -- a different, softer case
        Assert.False(Directory.Exists(Path.Combine(_root, "generate", $"{Package}.Tests")));
        Assert.Empty(ReadManifest());
    }

    [Fact]
    public void Run_ReadsFromACustomFillsDirectory_WhenPassed()
    {
        WriteGeneratedPackage();
        var customFills = Directory.CreateTempSubdirectory("ssisx-apply-tests-custom-fills-").FullName;
        try
        {
            var dir = Path.Combine(customFills, Package, "MoreTests");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "FooMoreTests.cs"), "class X {}\n");

            var exitCode = ApplyTestsCommand.Run(["--out", _root, "--fills", customFills]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(_root, "generate", $"{Package}.Tests", "MoreTests", "FooMoreTests.cs")));
        }
        finally
        {
            try { Directory.Delete(customFills, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Run_DoesNotReadTheTestOracleTestsFolder_ApplyFillsOwnFolder()
    {
        // fills/Pkg/Tests/ (TEST-ORACLE, ApplyFillsCommand's own folder) must never be treated as
        // fills/Pkg/MoreTests/ -- the two mechanisms are deliberately kept from ever colliding.
        WriteGeneratedPackage();
        var testsDir = Path.Combine(_root, "fills", Package, "Tests");
        Directory.CreateDirectory(testsDir);
        File.WriteAllText(Path.Combine(testsDir, "OracleTests.cs"), "class X {}\n");

        var exitCode = ApplyTestsCommand.Run(["--out", _root]);

        Assert.Equal(0, exitCode);
        Assert.Empty(ReadManifest());
        Assert.False(File.Exists(Path.Combine(_root, "generate", $"{Package}.Tests", "MoreTests", "OracleTests.cs")));
    }

    [Fact]
    public void Run_RequiresOut()
    {
        var exitCode = ApplyTestsCommand.Run([]);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Run_ReportsNothingToDo_WhenNoFillsDirectoryExistsAtAll()
    {
        var exitCode = ApplyTestsCommand.Run(["--out", _root]);
        Assert.Equal(0, exitCode);
        Assert.Empty(ReadManifest());
    }
}
