using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Runs the real <c>ssisx generate</c> against a representative fixture per supported shape and
/// actually COMPILES what it wrote.
///
/// <para><b>Why this exists.</b> The 2026-09-02 gap audit found five real bugs sitting under a
/// fully green 270-test suite, and the reason was structural: almost every test in this project
/// asserts emitted TEXT or a GAP COUNT. Only DifferentialExpressionTests ever compiled and ran
/// generated code, and it covers five expressions on the two original PoC packages. So "0 gaps
/// reported" and "the suite is green" could both hold while the generated project did not build
/// at all -- which had already happened at least twice here: the Package CS1061 row-type
/// mismatch, and the DFT_SortAndMergeJoin flow that reported zero gaps while referencing a
/// column that did not exist.</para>
///
/// <para>This closes that hole and no more. It does NOT prove behaviour -- a package can compile
/// and still produce wrong rows, which is what the dtexec-comparison fixtures are for. It proves
/// the cheaper property that was nonetheless being violated: if this tool reports a flow as
/// generated, the code it wrote is valid C# against the real Etl.Core.</para>
///
/// <para><b>Drives the CLI rather than calling PackageGenerator directly</b>, deliberately.
/// <c>PackageGenerator.Generate</c> returns paths relative to the PACKAGE folder, and the
/// solution plus the Directory.Build.props/Directory.Packages.props that make the tree buildable
/// are written by GenerateCommand's own WriteFixedFiles. Reimplementing that here would compile a
/// hand-assembled tree nobody ships, and would drift from the real one silently.</para>
///
/// <para><b>Skips when it cannot run, and the class comment is the warning.</b> It needs a built
/// ssisx, which is guaranteed once this repo's own solution has been built at least once.
/// Absence is an early return, matching IspacReaderTests' established pattern for build outputs
/// (this xUnit version has no Assert.Skip). A skipped run proves nothing -- run
/// scripts/Run-Tests.ps1 on a dev box.</para>
/// </summary>
public class GeneratedSolutionBuildTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static string ExtractorRoot => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFilePath())!, "..", ".."));

    private static string FixturesDir => Path.Combine(ExtractorRoot, "tests", "Ssis.Extract.Tests", "Fixtures");

    /// <summary>
    /// The hand-written runtime generated projects reference. Not produced by this tool -- a
    /// portable, in-repo copy lives at Tools/Etl.Core (sibling of Tools/SsisExtractor), refreshed
    /// from the actual development repo (D:\PoC\SSIS_Rewrite\src\Etl.Core) when that library
    /// changes. Resolved relative to this file's own path, not a hardcoded dev-machine absolute
    /// path, so this test runs on any clone rather than only this one machine.
    /// </summary>
    private static string EtlCoreProjectDir => Path.Combine(ExtractorRoot, "..", "Etl.Core");

    /// <summary>Prefers Release (what the survey scripts publish) but takes whatever is built.</summary>
    private static string? FindSsisx()
    {
        var cliBin = Path.Combine(ExtractorRoot, "src", "Ssis.Extract.Cli", "bin");
        if (!Directory.Exists(cliBin)) return null;
        return Directory.GetFiles(cliBin, "ssisx.exe", SearchOption.AllDirectories)
            .OrderByDescending(p => p.Contains("Release", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// One fixture per DISTINCT generated shape, chosen so a break in any emitter that took a
    /// real bug in the audit surfaces here. Deliberately not every fixture: each costs a real
    /// `dotnet build`, and a suite slow enough to skip is a suite that gets skipped.
    /// </summary>
    public static IEnumerable<object[]> Fixtures()
    {
        yield return ["SyntheticPostFlowSql.dtsx"];           // CSV source + Derived Column + pre/post SQL
        yield return ["SyntheticDerivedColumnReplace.dtsx"];  // in-place Derived Column (audit finding 1)
        yield return ["SyntheticMergeJoin.dtsx"];             // Sort + Merge Join (audit finding 3)
        yield return ["SyntheticUnionTwoSources.dtsx"];       // multi-source Union All (audit finding 4)
        yield return ["SyntheticLookupSingle.dtsx"];          // full-cache Lookup (audit finding 5)
        yield return ["SyntheticConditionalSplit.dtsx"];      // router + multiple sinks
        yield return ["SyntheticAggregate.dtsx"];             // Aggregate row source
        yield return ["SyntheticFlatFileDestination.dtsx"];   // flat-file sink + a SQL flow beside it
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void GeneratedProjectCompiles(string fixtureName)
    {
        var fixturePath = Path.Combine(FixturesDir, fixtureName);
        Assert.True(File.Exists(fixturePath), $"fixture missing: {fixturePath}");

        var ssisx = FindSsisx();
        if (ssisx is null || !Directory.Exists(EtlCoreProjectDir)) return; // see class doc comment

        var work = Path.Combine(Path.GetTempPath(), "ssisx-buildtest", Path.GetRandomFileName());
        try
        {
            // Exit 3 just means gaps were reported, which most fixtures legitimately have.
            var gen = Run(ssisx, $"generate --input \"{fixturePath}\" --out \"{work}\"");
            Assert.True(gen.ExitCode is 0 or 3, $"ssisx generate failed ({gen.ExitCode}):{Environment.NewLine}{gen.Output}");

            var generateDir = Path.Combine(work, "generate");
            var solution = Path.Combine(generateDir, "Generated.slnx");
            // A fixture whose every flow gapped writes no solution -- nothing to compile, and
            // that is another test's business, not a failure here.
            if (!File.Exists(solution)) return;

            CopyEtlCore(generateDir);

            var build = Run("dotnet", $"build \"{solution}\" --nologo -v quiet");

            // CS8795 is the Tier-2 seam mechanism deliberately failing the build until a human
            // fills it in. Everything else means this tool emitted code that does not compile.
            var realErrors = build.Output
                .Split('\n')
                .Where(l => l.Contains(": error ", StringComparison.Ordinal) && !l.Contains("CS8795", StringComparison.Ordinal))
                .Select(l => l.Trim())
                .Distinct()
                .ToList();

            Assert.True(realErrors.Count == 0,
                $"{fixtureName}: generated project did not compile ({realErrors.Count} error(s)):{Environment.NewLine}"
                + string.Join(Environment.NewLine, realErrors.Take(10)));

            // Only seam errors left? Then assert that is genuinely the reason, so a future
            // failure whose error lines this parser does not recognise cannot pass silently.
            if (build.ExitCode != 0) Assert.Contains("CS8795", build.Output, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static void CopyEtlCore(string generateDir)
    {
        var target = Path.Combine(generateDir, "Etl.Core");
        var source = Path.GetFullPath(EtlCoreProjectDir);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = file[(source.Length + 1)..];
            if (rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(milliseconds: 5 * 60 * 1000);
        return (process.HasExited ? process.ExitCode : -1, stdout + stderr);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { /* a locked build output is not a test failure */ }
        catch (UnauthorizedAccessException) { }
    }
}
