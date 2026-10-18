using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Serialization;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx apply-tests</c> -- copies hand-written "more tests" into an already-generated
/// package's own test project. See <c>Docs/AI-Test-Enrichment-Plan.md</c> for the full design;
/// this is deliberately the narrowest possible mirror of <see cref="ApplyFillsCommand"/>, not a
/// second copy of its machinery.
///
/// <b>This is NOT part of the gap-fill workflow.</b> A "more test" answers no <c>GapKind</c>, is
/// never listed in <c>gaps.json</c>, and never affects <c>PortfolioDigest</c>'s "generatable"
/// count -- the package this command is pointed at is already fully generatable and green before
/// this ever runs. It exists purely so an assistant can raise coverage past the deterministic
/// starter baseline, using <c>generate/&lt;Package&gt;/README.md</c>'s own "Test coverage notes"
/// section as its (cheap) context instead of re-reading every generated <c>.cs</c> test file.
///
/// Reads <c>fills[-library]/&lt;Package&gt;/MoreTests/*.cs</c> ONLY -- never
/// <c>fills/&lt;Package&gt;/Tests/*.cs</c> (that folder is <c>TEST-ORACLE</c>'s own, matched
/// against `gaps.json` by <c>ApplyFillsCommand</c>; reusing it here would make that command try
/// to resolve a `GapId` that does not exist and report a false `Orphaned`). Every file present is
/// copied UNCONDITIONALLY -- there is no `GapId` to check it against, so unlike a Tier-1/2 fill
/// there is nothing to be `Stale`/`Orphaned` about; the self-check this design leans on instead is
/// that a test referencing a renamed/removed method or column simply fails to compile the next
/// time the package is rebuilt (see <see cref="MoreTestStatus"/>'s own doc comment).
/// </summary>
internal static class ApplyTestsCommand
{
    /// <summary>Matches a "more test" provenance comment, e.g.
    /// <c>// ssisx-more-test: Author=me Date=2026-09-06 Targets=FooTransform.SCR_CleanseCustomerRow</c>.
    /// Audit-only -- never used to decide whether a file is applied (see this file's own class
    /// doc comment).</summary>
    private static readonly Regex ProvenanceCommentPattern =
        new(@"^\s*//\s*ssisx-more-test:\s*(?<fields>.+)$", RegexOptions.Compiled);

    private sealed record MoreTestProvenance(string? Author, string? Date, string? Targets);

    public static int Run(string[] args)
    {
        string? outDir = null;
        string? fillsDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = RequireValue(args, ref i, "--out"); break;
                case "--fills": fillsDir = RequireValue(args, ref i, "--fills"); break;
                default:
                    Console.Error.WriteLine($"error: unknown option '{args[i]}'. Run 'ssisx --help'.");
                    return 2;
            }
        }

        if (outDir is null)
        {
            Console.Error.WriteLine("error: apply-tests requires --out <dir> (the same --out 'ssisx generate' wrote).");
            return 2;
        }

        outDir = Path.GetFullPath(outDir);
        // Phase 1.5 (R6): nothing else in this command needs --out to already exist (a missing
        // fillsDir just means the scan below finds nothing, and StableJsonWriter.WriteToFile
        // happily creates any missing directory when it writes the manifest at the end) --
        // which used to mean this command exited 0 and CONJURED --out into existence for a path
        // that was never a real `ssisx generate` output at all. A nonexistent --out is always a
        // usage mistake for this command specifically (it only ever makes sense pointed at one).
        if (!Directory.Exists(outDir))
        {
            Console.Error.WriteLine($"error: --out '{outDir}' does not exist -- run 'ssisx generate --out {outDir}' first.");
            return ExitCodes.UsageOrInputError;
        }
        fillsDir = Path.GetFullPath(fillsDir ?? Path.Combine(outDir, "fills"));
        var generateDir = Path.Combine(outDir, "generate");

        var applied = new List<string>();
        var unattributed = new List<string>();
        var rejected = new List<string>();
        var unverified = new List<string>();
        var unverifiedReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknownPackages = new List<string>();
        var noTestProject = new List<string>();
        var records = new List<MoreTestRecordSpec>();

        if (Directory.Exists(fillsDir))
        {
            foreach (var packageDir in Directory.EnumerateDirectories(fillsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var moreTestsDir = Path.Combine(packageDir, "MoreTests");
                if (!Directory.Exists(moreTestsDir)) continue;

                var packageName = Path.GetFileName(packageDir);
                if (!Directory.Exists(Path.Combine(generateDir, packageName)))
                {
                    unknownPackages.Add(packageName);
                    continue;
                }

                var testProjectDir = Path.Combine(generateDir, $"{packageName}.Tests");
                if (!Directory.Exists(testProjectDir))
                {
                    // A package with zero starter tests (--skip-tests, or nothing to test at all)
                    // has no {Package}.Tests project for a "more test" to join at all -- reported,
                    // not silently skipped, since it usually means apply-tests was pointed at the
                    // wrong --out or run before `ssisx generate` produced this package's tests.
                    noTestProject.Add(packageName);
                    continue;
                }

                var targetDir = Path.Combine(testProjectDir, "MoreTests");
                var copied = new List<(string FileName, MoreTestProvenance? Provenance)>();
                foreach (var file in Directory.EnumerateFiles(moreTestsDir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var fileName = Path.GetFileName(file);
                    Directory.CreateDirectory(targetDir);
                    File.Copy(file, Path.Combine(targetDir, fileName), overwrite: true);
                    copied.Add((fileName, ExtractLeadingProvenance(File.ReadAllText(file))));
                }
                if (copied.Count == 0) continue;

                // Verify the package's test project with the copies in place. A file the compiler
                // blames is removed again from generate/ (the original under fills/ is never
                // touched) so one bad hand-written test cannot break the package's own starter
                // baseline. Anything the compiler cannot pin on these files is reported as
                // Unverified and left in place.
                var verification = VerifyMoreTests(testProjectDir, packageName, targetDir, copied.Select(c => c.FileName).ToList());

                foreach (var (fileName, provenance) in copied)
                {
                    var status = verification.Rejected.ContainsKey(fileName) ? MoreTestStatus.Rejected
                        : verification.Unverified ? MoreTestStatus.Unverified
                        : MoreTestStatus.Applied;
                    var label = $"{packageName}/MoreTests/{fileName}";
                    switch (status)
                    {
                        case MoreTestStatus.Applied:
                            if (provenance is null) unattributed.Add(label); else applied.Add(label);
                            break;
                        case MoreTestStatus.Rejected: rejected.Add(label); break;
                        case MoreTestStatus.Unverified: unverified.Add(label); break;
                    }

                    records.Add(new MoreTestRecordSpec
                    {
                        Package = packageName, FileName = fileName, Status = status,
                        Attributed = provenance is not null,
                        Author = provenance?.Author, Date = provenance?.Date, Targets = provenance?.Targets,
                        CompilerErrors = verification.Rejected.TryGetValue(fileName, out var errors) ? errors : null,
                    });
                }
                if (verification.Unverified)
                    unverifiedReasons[packageName] = verification.UnverifiedReason!;
            }
        }

        var manifestPath = Path.Combine(outDir, "tests-applied.json");
        StableJsonWriter.WriteToFile(
            records.OrderBy(r => r.Package, StringComparer.Ordinal).ThenBy(r => r.FileName, StringComparer.Ordinal).ToList(),
            manifestPath);

        Report(fillsDir, manifestPath, applied, unattributed, rejected, unverified, unverifiedReasons,
            unknownPackages, noTestProject);

        if (unknownPackages.Count > 0) return 1;
        // 3 = some files were not applied (rejected) or could not be verified: a warning for the
        // caller, the same meaning it has for apply-fills. Not a crash, and the package still builds.
        return rejected.Count > 0 || unverified.Count > 0 ? ExitCodes.BlockingGaps : 0;
    }

    private sealed record VerificationResult(
        Dictionary<string, List<string>> Rejected, bool Unverified, string? UnverifiedReason);

    /// <summary>Builds the package's test project with the given MoreTests copies in place and
    /// removes every copy the compiler blames, looping until the build passes or nothing left can
    /// be attributed to these files. Rebuilds after each removal, since removing one file can
    /// change which errors the compiler reports for the rest.</summary>
    private static VerificationResult VerifyMoreTests(
        string testProjectDir, string packageName, string targetDir, List<string> fileNames)
    {
        var csproj = Path.Combine(testProjectDir, $"{packageName}.Tests.csproj");
        var rejected = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var live = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(csproj))
            return new VerificationResult(rejected, true, $"no {packageName}.Tests.csproj to build against");

        while (live.Count > 0)
        {
            var result = BuildTestProject(csproj);
            if (result.Succeeded) return new VerificationResult(rejected, false, null);

            if (result.Failure is not null)
                return new VerificationResult(rejected, true, $"{result.Failure} {string.Join(" | ", result.OtherErrors.Take(3))}".Trim());

            var blamed = result.MoreTestErrors.Where(e => live.Contains(e.File)).ToList();
            if (blamed.Count == 0)
                return new VerificationResult(rejected, true,
                    "build failed for reasons not in these MoreTests files: " + string.Join(" | ", result.OtherErrors.Take(3)));

            foreach (var group in blamed.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
            {
                rejected[group.Key] = group.Select(e => e.Message).Distinct().ToList();
                live.Remove(group.Key);
                File.Delete(Path.Combine(targetDir, group.Key));
            }
        }

        return new VerificationResult(rejected, false, null);
    }

    private sealed record BuildResult(
        bool Succeeded,
        List<(string File, string Message)> MoreTestErrors,
        List<string> OtherErrors,
        string? Failure);

    // Matches "...MoreTests\Foo.cs(12,5): error CS1002: ..." (MSBuild's own compiler diagnostic
    // format; warnings count too, since generated projects use TreatWarningsAsErrors).
    private static readonly Regex CompilerErrorPattern =
        new(@"MoreTests[\\/](?<file>[^\\/()]+\.cs)\(\d+,\d+\):\s*error\s*(?<code>[A-Z]+\d+):\s*(?<msg>.+?)(\s\[.*\])?$",
            RegexOptions.Compiled | RegexOptions.Multiline);

    private static BuildResult BuildTestProject(string csproj)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "build", csproj, "-nologo", "-v:minimal", "-nodeReuse:false" })
            psi.ArgumentList.Add(arg);

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception ex)
        {
            return new BuildResult(false, [], [], $"could not start 'dotnet build': {ex.Message}");
        }
        if (process is null) return new BuildResult(false, [], [], "could not start 'dotnet build'");

        using (process)
        {
            // Both streams drained concurrently: reading one to EOF before the other can deadlock
            // once the child fills the other pipe's buffer. Same rule as GeneratedSolutionBuildTests.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new BuildResult(false, [], [], "dotnet build timed out after 10 minutes");
            }
            Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(30));
            var text = stdout.Result + "\n" + stderr.Result;

            if (process.ExitCode == 0) return new BuildResult(true, [], [], null);

            var moreTest = new List<(string, string)>();
            var other = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (!line.Contains(": error", StringComparison.Ordinal)) continue;
                if (!seen.Add(line)) continue;
                var m = CompilerErrorPattern.Match(line);
                if (m.Success) moreTest.Add((m.Groups["file"].Value, $"{m.Groups["code"].Value}: {m.Groups["msg"].Value}"));
                else other.Add(line);
            }
            return new BuildResult(false, moreTest, other, moreTest.Count == 0 && other.Count == 0
                ? $"dotnet build exited {process.ExitCode} with no error lines"
                : null);
        }
    }

    private static MoreTestProvenance? ExtractLeadingProvenance(string content)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var m = ProvenanceCommentPattern.Match(line);
            return m.Success ? ParseProvenance(m.Groups["fields"].Value) : null;
        }
        return null;
    }

    private static MoreTestProvenance ParseProvenance(string fields)
    {
        string? author = null, date = null, targets = null;
        foreach (var token in fields.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            var value = token[(eq + 1)..];
            switch (token[..eq])
            {
                case "Author": author = value; break;
                case "Date": date = value; break;
                case "Targets": targets = value; break;
            }
        }
        return new MoreTestProvenance(author, date, targets);
    }

    private static void Report(
        string fillsDir, string manifestPath, List<string> applied, List<string> unattributed,
        List<string> rejected, List<string> unverified, Dictionary<string, string> unverifiedReasons,
        List<string> unknownPackages, List<string> noTestProject)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"apply-tests: {applied.Count + unattributed.Count} file(s) applied from {fillsDir}");
        foreach (var line in applied) sb.AppendLine($"  + {line}");
        foreach (var line in unattributed) sb.AppendLine($"  + {line} (no // ssisx-more-test: comment -- undocumented addition)");

        if (rejected.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{rejected.Count} file(s) REJECTED -- the package's test project does not compile with them, so they were");
            sb.AppendLine("removed from generate/ again (the originals under fills/ are untouched). Fix the file and re-run apply-tests:");
            foreach (var line in rejected) sb.AppendLine($"  x {line}");
        }

        if (unverified.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("File(s) copied but NOT verified -- the test project build failed for a reason these files cannot");
            sb.AppendLine("be blamed for, so they were left in place:");
            foreach (var line in unverified) sb.AppendLine($"  ? {line}");
            foreach (var (package, reason) in unverifiedReasons) sb.AppendLine($"    {package}: {reason}");
        }

        if (unknownPackages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("More-tests for a package that was not generated into --out:");
            foreach (var line in unknownPackages) sb.AppendLine($"  ? {line}");
        }

        if (noTestProject.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("More-tests for a package with no {Package}.Tests project (was it generated with --skip-tests,");
            sb.AppendLine("or does it have zero starter tests at all?):");
            foreach (var line in noTestProject) sb.AppendLine($"  ? {line}");
        }

        if (applied.Count == 0 && unattributed.Count == 0 && unknownPackages.Count == 0 && noTestProject.Count == 0)
            sb.AppendLine("  (no fills[-library]/<Package>/MoreTests/*.cs found)");

        sb.AppendLine();
        sb.AppendLine($"Full audit trail: {manifestPath}");

        Console.Write(sb.ToString());
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value");
        return args[++i];
    }
}
