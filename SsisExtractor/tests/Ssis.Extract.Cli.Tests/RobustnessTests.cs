using System.Text.Json;
using Ssis.Extract.Cli;

namespace Ssis.Extract.Cli.Tests;

/// <summary>
/// Phase 0.2 of the independent-review fix plan (<c>C:\Users\yashpal.singh01\.claude\plans\snappy-skipping-grove.md</c>).
/// One <c>[Fact]</c> per repro case the review left under
/// <c>D:\tmp\independent-review-2026-09-25\robustness\cases\</c>, copied byte-for-byte into
/// <c>Fixtures/Robustness/&lt;case&gt;/</c> so the case survives that scratch directory being
/// cleaned up. Each asserts the POST-FIX behaviour the plan commits to for that case's own item,
/// and is <c>[Fact(Skip="Phase N")]</c> until that phase's commit actually lands -- flip the skip
/// off in the SAME commit that lands the fix, never before or after.
///
/// A case with no scheduled fix (its current behaviour is already correct, per PackageLoader's own
/// existing try/catch-per-unit design) is a real, unskipped regression guard from day one.
///
/// Calls <c>ExtractCommand.Run</c>/<c>GenerateCommand.Run</c> in-process against a temp <c>--out</c>
/// (both are <c>internal static</c>, already visible here via the project's own
/// <c>InternalsVisibleTo</c>) -- the same technique <c>ApplyFillsCommandTests</c> already uses, and
/// the strongest evidence tier available without shelling out to a built exe.
/// </summary>
public class RobustnessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ssisx-robustness-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string ThisFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static readonly string CasesDir =
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFilePath())!, "Fixtures", "Robustness"));

    private static string CaseDir(string name) => Path.Combine(CasesDir, name);

    private string NewOutDir(string sub = "out") => Path.Combine(_root, sub);

    private static (int Exit, string Out, string Err) RunCaptured(Func<int> action)
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errWriter);
        try
        {
            var exit = action();
            return (exit, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
    }

    // ---------------------------------------------------------------------
    // Phase 2.1 (R1) -- a cyclic precedence-constraint graph.
    // Today: HasCycle is computed and ignored; PackagePlanner falls back to declaration
    // order and generates the two tasks in the WRONG order with no gap at all.
    // After 2.1: a blocking "...PrecedenceConstraints" gap naming the cycle, nothing generated
    // for that container.
    // ---------------------------------------------------------------------
    [Fact]
    public void Cycle_ReportsABlockingGap_InsteadOfGeneratingTheWrongOrder()
    {
        var outDir = NewOutDir();
        var exit = RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("cycle"), "--out", outDir])).Exit;

        var gaps = ReadGaps(outDir);
        Assert.Contains(gaps, g => g.GetProperty("Reason").GetString()!.Contains("precedence constraints form a cycle", StringComparison.OrdinalIgnoreCase));
        Assert.True(exit is 3 or 4, $"expected a blocking-gap exit, got {exit}");
    }

    [Fact]
    public void SelfLoop_IsTreatedAsACycleToo()
    {
        var outDir = NewOutDir();
        RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("selfloop"), "--out", outDir]));

        var gaps = ReadGaps(outDir);
        Assert.Contains(gaps, g => g.GetProperty("Reason").GetString()!.Contains("precedence constraints form a cycle", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------
    // Phase 2.2 (R1 dangling) -- a precedence constraint whose To/From references a
    // nonexistent executable. Today: silently dropped by BuildDag, and the package turns
    // into a concurrent Task.WhenAll wave with no gap at all.
    // ---------------------------------------------------------------------
    [Fact]
    public void DanglingConstraint_ReportsABlockingGap_InsteadOfBecomingAConcurrentWave()
    {
        var outDir = NewOutDir();
        RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("dangling"), "--out", outDir]));

        var gaps = ReadGaps(outDir);
        Assert.Contains(gaps, g => g.GetProperty("Reason").GetString()!.Contains("unknown executable", StringComparison.OrdinalIgnoreCase));

        var generatedCs = FindGeneratedPackageClass(outDir);
        Assert.DoesNotContain("Task.WhenAll", generatedCs ?? "");
    }

    // ---------------------------------------------------------------------
    // Phase 2.8 (R5) -- duplicate ObjectName across two loose .dtsx files in one folder
    // (Package.dtsx vs. Package.Scripts.WORKING.dtsx). Today: first in ordinal PATH order
    // wins, so the "...WORKING" copy beats the real one. After 2.8: the real rule (a
    // .dtproj-loaded package wins; then filename==ObjectName; then newest; then path order).
    // ---------------------------------------------------------------------
    [Fact]
    public void DuplicateNames_KeepsPackageDtsx_NotTheWorkingCopy()
    {
        var outDir = NewOutDir();
        RunCaptured(() => ExtractCommand.Run(["--input", CaseDir("dupname"), "--out", outDir, "--recursive"]));

        var specPath = Path.Combine(outDir, "packages", "Package.spec.json");
        Assert.True(File.Exists(specPath), "expected packages/Package.spec.json to exist");
        using var doc = JsonDocument.Parse(File.ReadAllText(specPath));
        var sourcePath = doc.RootElement.GetProperty("SourceDtsxPath").GetString();
        Assert.EndsWith("Package.dtsx", sourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(sourcePath!.Contains("WORKING", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------
    // Phase 1.6 (L4) -- the duplicate-name warning goes to Console.Error today, which
    // PowerShell renders as a NativeCommandError even though it's benign. After 1.6: Console.Out.
    // ---------------------------------------------------------------------
    [Fact]
    public void DuplicateNameWarning_GoesToStdOut_NotStdErr()
    {
        var outDir = NewOutDir();
        var (_, stdOut, stdErr) = RunCaptured(() => ExtractCommand.Run(["--input", CaseDir("dupname"), "--out", outDir, "--recursive"]));

        Assert.Contains("duplicate package name", stdOut, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("duplicate package name", stdErr, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------
    // Phase 1.2 -- extract must not stop early on a BENIGN load failure (duplicate-name here).
    // Today: ReportCommand returns 2 on ANY loaded.Failures.Count > 0, and ExtractCommand
    // propagates that as an early `return 2`, so graph/conformance/testgen never run.
    // ---------------------------------------------------------------------
    [Fact]
    public void DuplicateNames_ExitsZero_AndStillRunsEveryDownstreamStage()
    {
        var outDir = NewOutDir();
        var exit = RunCaptured(() => ExtractCommand.Run(["--input", CaseDir("dupname"), "--out", outDir, "--recursive"])).Exit;

        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(Path.Combine(outDir, "metadata", "lineage")), "graph stage (lineage/) never ran");
        Assert.True(Directory.Exists(Path.Combine(outDir, "metadata", "conformance")), "conformance stage never ran");
        Assert.True(Directory.Exists(Path.Combine(outDir, "metadata", "testgen")), "testgen stage never ran");
    }

    // ---------------------------------------------------------------------
    // Phase 2.9 (R7) -- a .dtproj referencing a missing/corrupt .conmgr.
    // Today: a missing .conmgr is silently `continue`d (nothing reports it); a corrupt one
    // throws out of XDocument.Load and DtprojReader.Read, so PackageLoader's LoadProject
    // catch rejects the WHOLE .dtproj and blames "Line 1", losing every other package too.
    // After 2.9: a named LoadFailure(kind "conmgr") naming the .conmgr path, and the project's
    // other connection managers / packages still load.
    // ---------------------------------------------------------------------
    [Fact]
    public void MissingConmgr_ReportsANamedFailure_AndTheProjectStillLoads()
    {
        var outDir = NewOutDir();
        var (exit, _, stdErr) = RunCaptured(() => ExtractCommand.Run(["--input", CaseDir("conmgr_missing"), "--out", outDir, "--recursive"]));

        Assert.Contains(".conmgr", stdErr, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package_Legacy.spec.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package_Exports.spec.json")));
    }

    [Fact]
    public void CorruptConmgr_ReportsANamedFailure_AndTheProjectStillLoads()
    {
        var outDir = NewOutDir();
        var (exit, _, stdErr) = RunCaptured(() => ExtractCommand.Run(["--input", CaseDir("conmgr_corrupt"), "--out", outDir, "--recursive"]));

        Assert.Contains("CM_Shared.conmgr", stdErr, StringComparison.OrdinalIgnoreCase);
        // The project's OWN packages must still load despite one corrupt .conmgr -- the whole
        // .dtproj must not be rejected the way it is today.
        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package_Legacy.spec.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package_Exports.spec.json")));
    }

    // ---------------------------------------------------------------------
    // Phase 1.3 (R3) -- Run-Pipeline.ps1 aborts on the first hard failure row today
    // (Write-Error -ErrorAction Stop) instead of warning and continuing to the other,
    // good packages. This is a script-level fix (PowerShell), so it's exercised by the
    // script test in 0.4/1.3, not here -- but the CLI-level building block (a truncated
    // package must not crash extraction of its four healthy siblings) is asserted now,
    // unskipped, since PackageLoader already isolates this per-unit.
    // ---------------------------------------------------------------------
    [Fact]
    public void TruncatedPackageAmongGoodOnes_DoesNotAbortTheOthers()
    {
        var outDir = NewOutDir();
        var exit = RunCaptured(() => ExtractCommand.Run(["--input", CaseDir("pipeline_onebad"), "--out", outDir, "--recursive"])).Exit;

        // Package_Advanced.dtsx is truncated in this case; the other four (Package,
        // Package_Exports, Package_Legacy, Package_Transforms) must still be written.
        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package.spec.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package_Exports.spec.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package_Legacy.spec.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package_Transforms.spec.json")));
        Assert.False(File.Exists(Path.Combine(outDir, "packages", "Package_Advanced.spec.json")));

        var loadFailuresMd = File.ReadAllText(Path.Combine(outDir, "metadata", "report", "load-failures.md"));
        Assert.Contains("Package_Advanced.dtsx", loadFailuresMd);
    }

    // ---------------------------------------------------------------------
    // Baseline robustness (already correct today, PackageLoader's own per-unit try/catch) --
    // unskipped from day one as a regression guard against Phase 1/2 changes breaking it.
    // ---------------------------------------------------------------------
    [Fact]
    public void ZeroByteDtsx_IsRecordedAsAFailure_AndDoesNotAbortItsGoodSibling()
    {
        var outDir = NewOutDir();
        RunCaptured(() => ExtractCommand.Run(["--input", CaseDir("zerobyte"), "--out", outDir, "--recursive"]));

        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package_Legacy.spec.json")));
        Assert.False(File.Exists(Path.Combine(outDir, "packages", "Zero.spec.json")));
        var loadFailuresMd = File.ReadAllText(Path.Combine(outDir, "metadata", "report", "load-failures.md"));
        Assert.Contains("Zero.dtsx", loadFailuresMd);
    }

    [Fact]
    public void DtprojListingMissingDtsxFiles_RecordsEachAsAFailure_AndLoadsWhatExists()
    {
        var outDir = NewOutDir();
        RunCaptured(() => ExtractCommand.Run(["--input", CaseDir("dtproj_missing_dtsx"), "--out", outDir, "--recursive"]));

        // Package.dtsx / Package_Advanced.dtsx / Package_Transforms.dtsx are listed in the
        // .dtproj but absent on disk; Package_Exports.dtsx / Package_Legacy.dtsx are present.
        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package_Exports.spec.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "packages", "Package_Legacy.spec.json")));
        Assert.False(File.Exists(Path.Combine(outDir, "packages", "Package.spec.json")));

        var loadFailuresMd = File.ReadAllText(Path.Combine(outDir, "metadata", "report", "load-failures.md"));
        Assert.Contains("Package.dtsx", loadFailuresMd);
        Assert.Contains("Package_Advanced.dtsx", loadFailuresMd);
        Assert.Contains("Package_Transforms.dtsx", loadFailuresMd);
    }

    // ---------------------------------------------------------------------
    // Phase 3.1 (R2) -- generating the same package twice into the same --out with a
    // structural change (a Data Flow Task disabled between runs) leaves stale generated
    // files from the first run, producing a build that fails to compile.
    // After 3.1: the package's own generate/<Pkg>/** (minus Fills/TestData) is cleaned
    // before writing, so no stale file survives a structural change.
    // ---------------------------------------------------------------------
    [Fact]
    public void RegeneratingAfterDisablingADataFlowTask_LeavesNoStaleMappingFile()
    {
        var outDir = NewOutDir();
        // First run: Package_Transforms with every flow live -- establishes Mapping/DER_Enrich.cs.
        RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("pipeline_good"), "--out", outDir, "--recursive", "--package", "Package_Transforms"]));
        var derEnrichPath = FindFileUnder(outDir, "DER_Enrich.cs");
        Assert.True(derEnrichPath is not null, "expected the first run to produce Mapping/DER_Enrich.cs");

        // Second run: the byte-edited copy with that Data Flow Task disabled.
        RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("disable_dft"), "--out", outDir, "--package", "Package_Transforms"]));

        Assert.False(File.Exists(derEnrichPath), "stale Mapping/DER_Enrich.cs from the first run must not survive a structural regenerate");
    }

    // ---------------------------------------------------------------------
    // Phase 3.1 (R2) -- the clean above must not touch hand-maintained work product: Fills/ and
    // TestData/ are the two directories under a package's own generate/<Pkg>/ this tool only
    // ever READS (a decisions.json/seam fill, a locally-supplied sample file), never generates.
    // ---------------------------------------------------------------------
    [Fact]
    public void RegeneratingAPackage_PreservesFillsAndTestDataButRemovesEverythingElse()
    {
        var outDir = NewOutDir();
        RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("pipeline_good"), "--out", outDir, "--recursive", "--package", "Package_Transforms"]));

        var pkgDir = Path.Combine(outDir, "generate", "Package_Transforms");
        var fillsDir = Path.Combine(pkgDir, "Fills");
        var testDataDir = Path.Combine(pkgDir, "TestData");
        Directory.CreateDirectory(fillsDir);
        Directory.CreateDirectory(testDataDir);
        File.WriteAllText(Path.Combine(fillsDir, "Hand.Fills.cs"), "// hand-written, must survive");
        File.WriteAllText(Path.Combine(testDataDir, "sample.csv"), "a,b\r\n1,2\r\n");

        RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("pipeline_good"), "--out", outDir, "--recursive", "--package", "Package_Transforms"]));

        Assert.True(File.Exists(Path.Combine(fillsDir, "Hand.Fills.cs")), "Fills/ must survive a regenerate of its own package");
        Assert.True(File.Exists(Path.Combine(testDataDir, "sample.csv")), "TestData/ must survive a regenerate of its own package");
    }

    // ---------------------------------------------------------------------
    // Phase 3.1 (R2) -- a whole package folder Generated.slnx no longer references (renamed
    // since a previous run into this same --out, or scoped out via --package this time) is
    // reported so a human notices it, never silently deleted -- it may be the only place that
    // package's own hand-written Fills/TestData still live.
    // ---------------------------------------------------------------------
    [Fact]
    public void AStalePackageFolder_IsReportedButNeverDeleted()
    {
        var outDir = NewOutDir();
        RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("pipeline_good"), "--out", outDir, "--recursive", "--package", "Package_Transforms"]));

        var staleDir = Path.Combine(outDir, "generate", "SomePackageThatNoLongerExists");
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(Path.Combine(staleDir, "old.cs"), "// old, from a previous run");

        var (_, stdout, _) = RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("pipeline_good"), "--out", outDir, "--recursive", "--package", "Package_Transforms"]));

        Assert.Contains("stale package folder 'SomePackageThatNoLongerExists'", stdout);
        Assert.True(File.Exists(Path.Combine(staleDir, "old.cs")), "a stale package folder must be reported, never auto-deleted");
    }

    // ---------------------------------------------------------------------
    // Phase 3.2 (R8) -- generate-report.md/gaps.json/Generated.slnx are deleted at the START of
    // a run, not just written at the end, so a run that aborts before writing anything new can
    // never leave a PREVIOUS run's stale summary sitting next to a --out that no longer reflects
    // what's actually on disk.
    // ---------------------------------------------------------------------
    [Fact]
    public void ARunThatAbortsBeforeWritingAnything_StillRemovesThePreviousRunsStaleSummaryFiles()
    {
        var outDir = NewOutDir();

        RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("pipeline_good"), "--out", outDir, "--recursive", "--package", "Package_Transforms"]));
        var reportPath = Path.Combine(outDir, "generate-report.md");
        var gapsPath = Path.Combine(outDir, "gaps.json");
        var slnxPath = Path.Combine(outDir, "generate", "Generated.slnx");
        Assert.True(File.Exists(reportPath));
        Assert.True(File.Exists(gapsPath));
        Assert.True(File.Exists(slnxPath));

        // A typo'd --package matches nothing -- aborts with a usage error before ever reaching
        // PackageLoader's success path, let alone writing anything new.
        var (exit, _, _) = RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("pipeline_good"), "--out", outDir, "--recursive", "--package", "NoSuchPackage"]));
        Assert.Equal(2, exit);

        Assert.False(File.Exists(reportPath), "a stale generate-report.md from a previous run must not survive an aborted re-run");
        Assert.False(File.Exists(gapsPath), "a stale gaps.json from a previous run must not survive an aborted re-run");
        Assert.False(File.Exists(slnxPath), "a stale Generated.slnx from a previous run must not survive an aborted re-run");
    }

    // ---------------------------------------------------------------------
    // Phase 2.6 (R9) -- SSIS persists a boolean variable default as "-1" (or "1"), not just
    // the literal string "True" this code currently accepts exclusively.
    // ---------------------------------------------------------------------
    [Fact]
    public void BooleanGuardVariable_AcceptsNegativeOneAsTrue()
    {
        var outDir = NewOutDir();
        RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("boolguard"), "--out", outDir]));

        var gaps = ReadGaps(outDir);
        Assert.DoesNotContain(gaps, g => g.GetProperty("Reason").GetString()!.Contains("no resolvable declared type", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------
    // Phase 4.2 (G2) -- a LOCAL-DATA work packet's evidence names how many rows to skip
    // before the real data starts, and shows what a schema-correct file's own first two
    // lines look like, reusing the exact same synthesis `SampleDataEmitter` already trusts.
    // Package_Legacy's own real CM_FF_CustomersFixed (RaggedRight, HeaderRowsToSkip=1) is
    // the one real evidenced case for this shape in the tracked portfolio.
    // ---------------------------------------------------------------------
    [Fact]
    public void LocalFileSourceDataPacket_NamesHeaderRowsToSkip_AndShowsASchemaCorrectPreview()
    {
        var outDir = NewOutDir();
        RunCaptured(() => GenerateCommand.Run(["--input", CaseDir("pipeline_good"), "--out", outDir, "--recursive", "--package", "Package_Legacy"]));

        var packetPath = Directory.EnumerateFiles(Path.Combine(outDir, "gaps", "Package_Legacy"), "LOCAL-DATA_*.md").FirstOrDefault();
        Assert.True(packetPath is not null, "expected a LOCAL-DATA work packet for Package_Legacy's own fixed-width source");
        var content = File.ReadAllText(packetPath!);

        Assert.Contains("**Rows to skip before data (HeaderRowsToSkip):** 1", content);
        Assert.Contains("### What a schema-correct file looks like (synthesized, not real data)", content);
        Assert.Contains("SKIP", content); // the real control record's own first synthesized line
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
    private static List<JsonElement> ReadGaps(string outDir)
    {
        var gapsPath = Path.Combine(outDir, "gaps.json");
        if (!File.Exists(gapsPath)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(gapsPath));
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private static string? FindGeneratedPackageClass(string outDir)
    {
        var generateDir = Path.Combine(outDir, "generate");
        if (!Directory.Exists(generateDir)) return null;
        var candidate = Directory.EnumerateFiles(generateDir, "*.cs", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == Path.GetFileName(Path.GetDirectoryName(f)));
        return candidate is null ? null : File.ReadAllText(candidate);
    }

    private static string? FindFileUnder(string outDir, string fileName)
    {
        var generateDir = Path.Combine(outDir, "generate");
        if (!Directory.Exists(generateDir)) return null;
        return Directory.EnumerateFiles(generateDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }
}
