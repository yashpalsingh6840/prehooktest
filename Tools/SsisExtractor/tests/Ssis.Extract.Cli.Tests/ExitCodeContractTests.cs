using Ssis.Extract.Cli;

namespace Ssis.Extract.Cli.Tests;

/// <summary>
/// Phase 1.5 of the independent-review fix plan (R6, `C:\Users\yashpal.singh01\.claude\plans\
/// snappy-skipping-grove.md`) -- the exit-code contract in <see cref="ExitCodes"/>. Calls
/// GenerateCommand/ApplyFillsCommand/ApplyTestsCommand.Run in-process, the same technique
/// ApplyFillsCommandTests/RobustnessTests already use.
/// </summary>
public class ExitCodeContractTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ssisx-exitcode-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string ThisFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static readonly string FixturesDir = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));

    private static (int Exit, string Out) RunCaptured(Func<int> action)
    {
        var stdout = Console.Out;
        using var outWriter = new StringWriter();
        Console.SetOut(outWriter);
        try
        {
            var exit = action();
            return (exit, outWriter.ToString());
        }
        finally
        {
            Console.SetOut(stdout);
        }
    }

    [Fact]
    public void Generate_ReturnsNothingWritten_WhenEveryFlowIsBlocked()
    {
        var input = Path.Combine(FixturesDir, "SyntheticAggregateThenOleDbCommand.dtsx");
        var outDir = Path.Combine(_root, "out");

        var (exit, _) = RunCaptured(() => GenerateCommand.Run(["--input", input, "--out", outDir]));

        Assert.Equal(ExitCodes.NothingWritten, exit);
        var reportPath = Path.Combine(outDir, "generate-report.md");
        Assert.True(File.Exists(reportPath));
        // 0 real per-package files -- only the always-written fixed files (Directory.Build.props
        // etc.) exist, confirmed by there being no per-package subfolder under generate/.
        var generateDir = Path.Combine(outDir, "generate");
        var packageDirs = Directory.Exists(generateDir)
            ? Directory.GetDirectories(generateDir).Where(d => Path.GetFileName(d) is not ("Etl.Core" or "Shared"))
            : [];
        Assert.Empty(packageDirs);
    }

    [Fact]
    public void Generate_ReturnsUsageError_WhenEtlCoreIsGivenButMissing()
    {
        var input = Path.Combine(FixturesDir, "SyntheticIdentifierSanitization.dtsx");
        var outDir = Path.Combine(_root, "out");
        var missingEtlCore = Path.Combine(_root, "does-not-exist");

        var (exit, _) = RunCaptured(() => GenerateCommand.Run(["--input", input, "--out", outDir, "--etl-core", missingEtlCore]));

        Assert.Equal(ExitCodes.UsageOrInputError, exit);
        // Nothing should have been written at all -- a hard usage error is checked up front,
        // before Generated.slnx (or anything else) is ever produced.
        Assert.False(Directory.Exists(Path.Combine(outDir, "generate")));
    }

    [Fact]
    public void ApplyFills_ReturnsUsageError_WhenFillsIsExplicitlyGivenButMissing()
    {
        var outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "gaps.json"), "[]");
        var missingFills = Path.Combine(_root, "no-such-fills-dir");

        var (exit, _) = RunCaptured(() => ApplyFillsCommand.Run(["--out", outDir, "--fills", missingFills]));

        Assert.Equal(ExitCodes.UsageOrInputError, exit);
    }

    [Fact]
    public void ApplyTests_ReturnsUsageError_AndDoesNotCreateOutDir_WhenOutDirIsMissing()
    {
        var outDir = Path.Combine(_root, "does-not-exist-yet");

        var (exit, _) = RunCaptured(() => ApplyTestsCommand.Run(["--out", outDir]));

        Assert.Equal(ExitCodes.UsageOrInputError, exit);
        Assert.False(Directory.Exists(outDir), "apply-tests must not conjure --out into existence for a path that was never a real generate output");
    }
}
