using System.Diagnostics;
using System.Runtime.CompilerServices;

// Measured, not assumed: this project's own test suite ran 1m21s-1m28s in isolation but 10+
// minutes to 2.5h+ when run as part of the WHOLE Ssis.Extract.Codegen.Tests project, even after
// fixing two other real, independent contributing bugs (Run()'s own sequential stdout/stderr
// ReadToEnd deadlock risk, and each fixture's own unconstrained NuGet restore -- see both fixes
// in this file). Isolating GeneratedSolutionBuildTests ALONE (`--filter
// FullyQualifiedName~GeneratedSolutionBuildTests`) reliably reproduced the fast ~1m20s baseline
// every time; running the full project reliably reproduced the multi-minute stall every time.
// xUnit's own DEFAULT test-collection parallelism (every test CLASS is its own collection, and
// collections run concurrently with each other unless told otherwise) is what closes that gap:
// this class alone spawns 10+ real `dotnet build`/`ssisx generate` child processes, sequentially
// within itself, but xUnit was letting that run AT THE SAME WALL-CLOCK TIME as the other ~335
// fast unit tests' own collections -- MSBuild-node/NuGet-lock contention from that overlap, not
// from any unrelated external dotnet activity, is the actual mechanism. Disabling parallelization
// for the whole assembly is the standard xUnit answer to "a test that spawns real, expensive
// subprocesses must not run concurrently with anything else in this process" -- the other ~335
// tests are each sub-millisecond, so running them strictly sequentially costs nothing measurable.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

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

    /// <summary>
    /// This test's own runtime has been wildly inconsistent (1-2 minutes vs. 10+ minutes to
    /// 2.5h+, same machine/code) because every one of the 10+1 fixture builds below triggers its
    /// own INDEPENDENT, full implicit-restore <c>dotnet build</c> in a brand-new temp directory --
    /// even though every fixture's own emitted <c>Directory.Packages.props</c> is byte-identical
    /// (same <c>GenerateCommand.WriteFixedFiles</c> call) and every package version it needs is
    /// already in the local global packages cache. Forcing restore to resolve ONLY from that
    /// cache (a plain folder is a valid NuGet source -- the global packages folder's own layout
    /// already satisfies it) removes network dependency from the restore step entirely, which is
    /// the actual mechanism repeatedly exposed to whatever was flaky, whether that turns out to
    /// be network-bound or not. <c>NUGET_PACKAGES</c> is honored if set (matches what a CI runner
    /// might configure); otherwise this falls back to the same path
    /// <c>dotnet nuget locals global-packages --list</c> reports on a normal dev box.
    ///
    /// <para><b>Deliberately scoped to THIS TEST'S OWN <c>dotnet build</c> calls only</b> -- never
    /// applied to the `ssisx generate` calls (which don't restore anything themselves), and never
    /// copied into <c>GenerateCommand</c>'s own emitted output or any client-facing doc. A real
    /// client machine restoring a freshly generated project for the first time needs genuine
    /// `nuget.org` access; this override only helps a machine (like this dev box, or a CI runner
    /// seeded once) that has already restored everything at least once before.</para>
    /// </summary>
    private static string OfflineRestoreSourcesArg
    {
        get
        {
            var packagesDir = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            return $"-p:RestoreSources=\"{packagesDir}\"";
        }
    }

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
        yield return ["SyntheticDataConversion.dtsx"];        // {Package}.Tests sibling project (TransformTestEmitter)
    }

    /// <summary>Not in <see cref="Fixtures"/> above -- this PoC's own real, checked-in
    /// <c>SSIS/SyntheticParallelShapes.dtsx</c>, not a <c>tests/Ssis.Extract.Tests/Fixtures/</c>
    /// synthetic one, so it needs its own path. Several independent flows share the schema-default
    /// "Flat File Source" component name across different Data Flow Tasks -- caught a real
    /// cross-flow row-type mismatch in the "Source -- CSV" starter test's own matching logic (see
    /// <c>ComponentMethodEntry.RowTypeName</c>'s doc comment) that every fixture in
    /// <see cref="Fixtures"/> was too small to ever exercise.</summary>
    [Fact]
    public void GeneratedProjectCompiles_SyntheticParallelShapes()
    {
        var fixturePath = Path.Combine(ExtractorRoot, "..", "..", "..", "SSIS_Packages", "SSIS", "SyntheticParallelShapes.dtsx");
        Assert.True(File.Exists(fixturePath), $"fixture missing: {fixturePath}");

        var ssisx = FindSsisx();
        if (ssisx is null || !Directory.Exists(EtlCoreProjectDir)) return; // see class doc comment

        var work = Path.Combine(Path.GetTempPath(), "ssisx-buildtest", Path.GetRandomFileName());
        try
        {
            var gen = Run(ssisx, $"generate --input \"{fixturePath}\" --out \"{work}\"");
            Assert.True(gen.ExitCode is 0 or 3, $"ssisx generate failed ({gen.ExitCode}):{Environment.NewLine}{gen.Output}");

            var generateDir = Path.Combine(work, "generate");
            var solution = Path.Combine(generateDir, "Generated.slnx");
            if (!File.Exists(solution)) return;

            CopyEtlCore(generateDir);

            // -nodeReuse:false is load-bearing, not cosmetic: with reuse ON (MSBuild's own
            // default), this build can hand off to a PERSISTENT background server node that
            // outlives the `dotnet build` process object Run() is watching -- if that node
            // inherited the redirected stdout/stderr pipe handles (confirmed via a live process
            // dump: a `/nodemode:1 /nodeReuse:true` node was still alive and holding output open
            // minutes after this fixture's own build had visibly finished writing every file),
            // the pipe never sees EOF and ReadToEndAsync() blocks forever, even with the
            // concurrent-read fix above. Forcing a non-reused, transient node for these
            // specific spawned builds is what actually closes the pipe on exit.
            var build = Run("dotnet", $"build \"{solution}\" --nologo -v quiet -nodeReuse:false {OfflineRestoreSourcesArg}");
            var realErrors = build.Output
                .Split('\n')
                .Where(l => l.Contains(": error ", StringComparison.Ordinal) && !l.Contains("CS8795", StringComparison.Ordinal))
                .Select(l => l.Trim())
                .Distinct()
                .ToList();

            Assert.True(realErrors.Count == 0,
                $"SyntheticParallelShapes.dtsx: generated project did not compile ({realErrors.Count} error(s)):{Environment.NewLine}"
                + string.Join(Environment.NewLine, realErrors.Take(10)));

            if (build.ExitCode != 0) Assert.Contains("CS8795", build.Output, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(work);
        }
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

            // -nodeReuse:false is load-bearing, not cosmetic: with reuse ON (MSBuild's own
            // default), this build can hand off to a PERSISTENT background server node that
            // outlives the `dotnet build` process object Run() is watching -- if that node
            // inherited the redirected stdout/stderr pipe handles (confirmed via a live process
            // dump: a `/nodemode:1 /nodeReuse:true` node was still alive and holding output open
            // minutes after this fixture's own build had visibly finished writing every file),
            // the pipe never sees EOF and ReadToEndAsync() blocks forever, even with the
            // concurrent-read fix above. Forcing a non-reused, transient node for these
            // specific spawned builds is what actually closes the pipe on exit.
            var build = Run("dotnet", $"build \"{solution}\" --nologo -v quiet -nodeReuse:false {OfflineRestoreSourcesArg}");

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

        // Read BOTH streams concurrently, not stdout-then-stderr -- this was a real, reproduced
        // deadlock/backpressure risk, not a theoretical one: if the child writes enough to
        // whichever stream isn't being drained yet (e.g. MSBuild's own locked-file retry
        // warnings, ~2.5KB from 10 retries alone) to fill the OS pipe buffer, the CHILD blocks on
        // that write while the PARENT is still blocked in ReadToEnd() on the other stream, and
        // neither makes progress until something external intervenes. This is the actual
        // documented anti-pattern behind System.Diagnostics.Process's own stream-redirection
        // warning, and matches this test's own observed symptom exactly: a multi-minute-to-hours
        // stall with near-zero CPU on the spawned process (blocked, not computing).
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(milliseconds: 5 * 60 * 1000))
        {
            // WaitForExit(int) does NOT kill the process when the timeout elapses -- leaving a
            // stuck child running forever, still holding whatever file locks it had (this is
            // exactly how a stale `testhost`/`dotnet build` process was found alive HOLDING A
            // BUILD OUTPUT LOCK more than 25 minutes after this test's own `dotnet test`
            // invocation should have finished, causing an unrelated LATER build to fail with
            // MSB3026/MSB3027). Kill the whole tree rather than abandon it.
            try { process.Kill(entireProcessTree: true); } catch { /* already exited by the time we got here */ }
            process.WaitForExit();
            return (-1, stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult());
        }

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult());
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { /* a locked build output is not a test failure */ }
        catch (UnauthorizedAccessException) { }
    }
}
