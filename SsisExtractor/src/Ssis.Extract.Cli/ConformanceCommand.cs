using System.Globalization;
using System.Text;
using System.Text.Json;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Serialization;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx conformance</c> -- gate 1 of <c>Migration-Validation-Plan.md</c>: generate the
/// obligations a replacement must satisfy, and (when claim files exist) report how many are
/// actually accounted for.
///
/// <b>This is the extractor's first output that something else consumes rather than a human
/// reads.</b> Every other command describes the old package; this one produces a contract
/// the rewrite is measured against, and an exit code CI can gate on.
///
/// <b>The claim files are never overwritten.</b> Rules are regenerated from the .dtsx on
/// every run and are disposable; claims are human work product accumulated across a
/// migration. A stub is written only when none exists -- see <see cref="WriteStubIfAbsent"/>.
/// </summary>
internal static class ConformanceCommand
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static int Run(string[] args)
    {
        string? input = null;
        string? outDir = null;
        string? claimsDir = null;
        var recursive = false;
        var check = false;
        var packageNames = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            try
            {
                switch (args[i])
                {
                    case "--input": input = RequireValue(args, ref i, "--input"); break;
                    case "--out": outDir = RequireValue(args, ref i, "--out"); break;
                    case "--claims": claimsDir = RequireValue(args, ref i, "--claims"); break;
                    case "--recursive": recursive = true; break;
                    case "--check": check = true; break;
                    case "--package":
                        var pkgArg = RequireValue(args, ref i, "--package");
                        packageNames.AddRange(pkgArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                        break;
                    default:
                        Console.Error.WriteLine($"error: unknown conformance option '{args[i]}'");
                        return 2;
                }
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 2;
            }
        }

        if (input is null || outDir is null)
        {
            Console.Error.WriteLine("error: --input and --out are required. Run 'ssisx --help'.");
            return 2;
        }
        if (!Path.Exists(input))
        {
            Console.Error.WriteLine($"error: input path not found: {input}");
            return 2;
        }

        input = Path.GetFullPath(input);
        outDir = Path.GetFullPath(outDir);
        // Claims default to living beside the rules, which makes the zero-config flow work:
        // first run writes stubs, the author edits them in place, later runs read them back.
        claimsDir = Path.GetFullPath(claimsDir ?? Path.Combine(outDir, "conformance", "claims"));

        try
        {
            using var loaded = PackageLoader.Load(input, noRedact: false, recursive, packageNames);
            var packages = loaded.Packages;

            if (packageNames.Count > 0 && packages.Count == 0)
            {
                Console.Error.WriteLine("error: --package matched no packages under this --input -- nothing to check.");
                return 2;
            }

            var rulesDir = Path.Combine(outDir, "conformance");
            Directory.CreateDirectory(rulesDir);
            Directory.CreateDirectory(claimsDir);

            var reports = new List<ConformanceReportSpec>();
            var allChecked = new List<(string Package, ConformanceChecker.CheckedRule Checked)>();
            var allOrphans = new List<(string Package, ImplementationClaimSpec Claim)>();
            var gateFailed = false;

            foreach (var pkg in packages)
            {
                var rules = ConformanceRulesBuilder.Build(pkg);
                var safeName = SanitizeFileName(pkg.ObjectName);

                StableJsonWriter.WriteToFile(rules, Path.Combine(rulesDir, $"{safeName}.rules.json"));

                var claimsPath = Path.Combine(claimsDir, $"{safeName}.claims.json");
                var stubWritten = WriteStubIfAbsent(claimsPath, rules);

                var claims = ReadClaims(claimsPath);
                var result = ConformanceChecker.Check(pkg.ObjectName, rules, claims);
                reports.Add(result.Report);
                allChecked.AddRange(result.Rules.Select(r => (pkg.ObjectName, r)));
                allOrphans.AddRange(result.OrphanedClaims.Select(c => (pkg.ObjectName, c)));

                if (!ConformanceChecker.Passes(result.Report)) gateFailed = true;

                Console.WriteLine($"  {pkg.ObjectName}: {rules.Count} rule(s), {result.Report.AccountedForPercent:0.##}% accounted for" +
                                  $" ({result.Report.Implemented} implemented, {result.Report.NotApplicable} n/a, {result.Report.Pending} pending, {result.Report.Unclaimed} unclaimed" +
                                  $"{(result.Report.InvalidClaims > 0 ? $", {result.Report.InvalidClaims} INVALID" : "")}" +
                                  $"{(result.Report.OrphanedClaims > 0 ? $", {result.Report.OrphanedClaims} orphaned" : "")})" +
                                  $"{(stubWritten ? "  [claims stub created]" : "")}");
            }

            StableJsonWriter.WriteToFile(reports.OrderBy(r => r.PackageName, StringComparer.Ordinal).ToList(), Path.Combine(rulesDir, "conformance-report.json"));
            WriteRulesCsv(allChecked, Path.Combine(rulesDir, "conformance-rules.csv"));
            WriteReportMd(reports, allChecked, allOrphans, Path.Combine(rulesDir, "conformance-report.md"));

            Console.WriteLine($"wrote conformance rules + report for {packages.Count} package(s) -> {rulesDir}");
            Console.WriteLine($"  claim files: {claimsDir}  (edit these -- ssisx never overwrites an existing one)");

            if (check && gateFailed)
            {
                Console.Error.WriteLine("error: gate 1 FAILED -- not every obligation is accounted for (see conformance-report.md).");
                return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Writes an all-<c>Pending</c> claim file only when none exists. Refusing to overwrite
    /// is the whole safety property of this command: the claim file is the migration's audit
    /// trail, and regenerating rules after a package edit must never silently discard the
    /// decisions already recorded against it.
    /// </summary>
    private static bool WriteStubIfAbsent(string claimsPath, List<ConformanceRuleSpec> rules)
    {
        if (File.Exists(claimsPath)) return false;
        StableJsonWriter.WriteToFile(ConformanceChecker.StubClaims(rules), claimsPath);
        return true;
    }

    private static List<ImplementationClaimSpec> ReadClaims(string claimsPath)
    {
        if (!File.Exists(claimsPath)) return [];
        var json = File.ReadAllText(claimsPath);
        return JsonSerializer.Deserialize<List<ImplementationClaimSpec>>(json, ReadOptions) ?? [];
    }

    private static void WriteRulesCsv(List<(string Package, ConformanceChecker.CheckedRule Checked)> rows, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Package,RuleId,Category,Outcome,MachineVerifiable,Location,Requirement,Evidence,ImplementedBy,Note,Problem");
        foreach (var (pkg, c) in rows.OrderBy(r => r.Package, StringComparer.Ordinal).ThenBy(r => r.Checked.Rule.RuleId, StringComparer.Ordinal))
        {
            sb.AppendLine(string.Join(",",
                Csv(pkg), Csv(c.Rule.RuleId), Csv(c.Rule.Category), Csv(c.Outcome),
                c.Rule.MachineVerifiable, Csv(c.Rule.Location), Csv(c.Rule.Requirement), Csv(c.Rule.Evidence ?? ""),
                Csv(c.Claim?.ImplementedBy ?? ""), Csv(c.Claim?.Note ?? ""), Csv(c.Problem ?? "")));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteReportMd(
        List<ConformanceReportSpec> reports,
        List<(string Package, ConformanceChecker.CheckedRule Checked)> allChecked,
        List<(string Package, ImplementationClaimSpec Claim)> allOrphans,
        string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gate 1 — spec conformance");
        sb.AppendLine();
        sb.AppendLine("Generated by `ssisx conformance` from the extracted package spec —");
        sb.AppendLine("gate 1 of `Migration-Validation-Plan.md` §3. Each row below is an obligation the");
        sb.AppendLine("replacement must satisfy, joined against the hand-maintained claim files.");
        sb.AppendLine();
        sb.AppendLine("**This gate proves nothing is *missing*. It does not prove anything is *correct*** —");
        sb.AppendLine("that is gate 2 (expression unit tests) and gate 4 (shadow-run row diff).");
        sb.AppendLine();

        sb.AppendLine("| Package | Rules | Implemented | N/A | Pending | Unclaimed | Invalid | Orphaned | Accounted for |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in reports.OrderBy(r => r.PackageName, StringComparer.Ordinal))
        {
            sb.AppendLine($"| {r.PackageName} | {r.TotalRules} | {r.Implemented} | {r.NotApplicable} | {r.Pending} | {r.Unclaimed} | {r.InvalidClaims} | {r.OrphanedClaims} | {r.AccountedForPercent:0.##}% |");
        }
        sb.AppendLine();

        sb.AppendLine("## Obligations by category");
        sb.AppendLine();
        sb.AppendLine("| Package | Category | Total | Outstanding |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var group in allChecked
                     .GroupBy(r => (r.Package, r.Checked.Rule.Category))
                     .OrderBy(g => g.Key.Package, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Category, StringComparer.Ordinal))
        {
            var outstanding = group.Count(r => r.Checked.Outcome is not (ConformanceChecker.StatusImplemented or ConformanceChecker.StatusNotApplicable));
            sb.AppendLine($"| {group.Key.Package} | {group.Key.Category} | {group.Count()} | {outstanding} |");
        }
        sb.AppendLine();

        var manual = allChecked.Where(r => !r.Checked.Rule.MachineVerifiable).ToList();
        if (manual.Count > 0)
        {
            sb.AppendLine("## Requires a human, not a test");
            sb.AppendLine();
            sb.AppendLine("These obligations cannot be satisfied by writing code against a generated assertion —");
            sb.AppendLine("someone has to read the extracted source and port it deliberately");
            sb.AppendLine("(`Migration-Validation-Plan.md` §13). They are excluded from no percentage; they");
            sb.AppendLine("are called out so a green number is never mistaken for one.");
            sb.AppendLine();
            foreach (var (pkg, c) in manual.OrderBy(r => r.Checked.Rule.RuleId, StringComparer.Ordinal))
            {
                sb.AppendLine($"- **{pkg}** · `{c.Rule.RuleId}` — {c.Rule.Requirement}  ");
                sb.AppendLine($"  _{c.Rule.Evidence}_ — **{c.Outcome}**");
            }
            sb.AppendLine();
        }

        // Invalid and Unclaimed are split deliberately. On a package nobody has started,
        // EVERY rule is unclaimed -- folding those into one list buries the handful of
        // genuinely malformed claims, which are the ones somebody has to act on today.
        var invalid = allChecked.Where(r => r.Checked.Outcome == "Invalid").ToList();
        if (invalid.Count > 0)
        {
            sb.AppendLine("## Invalid claims — fix these first");
            sb.AppendLine();
            foreach (var (pkg, c) in invalid.OrderBy(r => r.Checked.Rule.RuleId, StringComparer.Ordinal))
            {
                sb.AppendLine($"- **{pkg}** · `{c.Rule.RuleId}` — {c.Problem}");
            }
            sb.AppendLine();
        }

        var unclaimed = allChecked.Where(r => r.Checked.Outcome == "Unclaimed").ToList();
        if (unclaimed.Count > 0)
        {
            sb.AppendLine("## Unclaimed obligations");
            sb.AppendLine();
            sb.AppendLine("No claim exists for these. On a package nobody has started that is simply every");
            sb.AppendLine("rule; on one already in progress it means the rules were regenerated after the");
            sb.AppendLine("package changed and the new obligations have not been reviewed.");
            sb.AppendLine();
            foreach (var group in unclaimed.GroupBy(r => r.Package).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"**{group.Key}** ({group.Count()})");
                sb.AppendLine();
                foreach (var (_, c) in group.OrderBy(r => r.Checked.Rule.RuleId, StringComparer.Ordinal))
                {
                    sb.AppendLine($"- `{c.Rule.RuleId}`");
                }
                sb.AppendLine();
            }
        }

        if (allOrphans.Count > 0)
        {
            sb.AppendLine("## Orphaned claims");
            sb.AppendLine();
            sb.AppendLine("Claims naming a rule that no longer exists — the package changed underneath them.");
            sb.AppendLine("Re-review rather than delete: the obligation may have moved, not vanished.");
            sb.AppendLine();
            foreach (var (pkg, claim) in allOrphans.OrderBy(o => o.Claim.RuleId, StringComparer.Ordinal))
            {
                sb.AppendLine($"- **{pkg}** · `{claim.RuleId}` (was claimed **{claim.Status}**)");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Every obligation");
        sb.AppendLine();
        sb.AppendLine("| Package | Rule | Category | Outcome | Requirement | Evidence |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var (pkg, c) in allChecked
                     .OrderBy(r => r.Package, StringComparer.Ordinal)
                     .ThenBy(r => r.Checked.Rule.RuleId, StringComparer.Ordinal))
        {
            sb.AppendLine($"| {pkg} | `{c.Rule.RuleId}` | {c.Rule.Category} | {c.Outcome} | {Md(c.Rule.Requirement)} | {Md(c.Rule.Evidence ?? "")} |");
        }

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Pipes break a Markdown table cell; newlines break the row entirely.</summary>
    private static string Md(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? $"\"{s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ")}\""
            : s;

    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"{flag} requires a value");
        }
        i++;
        return args[i];
    }
}
