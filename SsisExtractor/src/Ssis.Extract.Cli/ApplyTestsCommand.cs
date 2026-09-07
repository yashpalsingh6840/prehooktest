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
    /// <c>// ssisx-more-test: Author=me Date=2026-09-06 Targets=FooTransform.Fill_Bar</c>.
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
        fillsDir = Path.GetFullPath(fillsDir ?? Path.Combine(outDir, "fills"));
        var generateDir = Path.Combine(outDir, "generate");

        var applied = new List<string>();
        var unattributed = new List<string>();
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
                foreach (var file in Directory.EnumerateFiles(moreTestsDir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var fileName = Path.GetFileName(file);
                    var provenance = ExtractLeadingProvenance(File.ReadAllText(file));

                    Directory.CreateDirectory(targetDir);
                    File.Copy(file, Path.Combine(targetDir, fileName), overwrite: true);

                    if (provenance is null) unattributed.Add($"{packageName}/MoreTests/{fileName}");
                    else applied.Add($"{packageName}/MoreTests/{fileName}");

                    records.Add(new MoreTestRecordSpec
                    {
                        Package = packageName, FileName = fileName, Status = MoreTestStatus.Applied,
                        Attributed = provenance is not null,
                        Author = provenance?.Author, Date = provenance?.Date, Targets = provenance?.Targets,
                    });
                }
            }
        }

        var manifestPath = Path.Combine(outDir, "tests-applied.json");
        StableJsonWriter.WriteToFile(
            records.OrderBy(r => r.Package, StringComparer.Ordinal).ThenBy(r => r.FileName, StringComparer.Ordinal).ToList(),
            manifestPath);

        Report(fillsDir, manifestPath, applied, unattributed, unknownPackages, noTestProject);

        return unknownPackages.Count > 0 ? 1 : 0;
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
        List<string> unknownPackages, List<string> noTestProject)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"apply-tests: {applied.Count + unattributed.Count} file(s) applied from {fillsDir}");
        foreach (var line in applied) sb.AppendLine($"  + {line}");
        foreach (var line in unattributed) sb.AppendLine($"  + {line} (no // ssisx-more-test: comment -- undocumented addition)");

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
