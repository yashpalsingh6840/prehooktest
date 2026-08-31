using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ssis.Extract.Model.Analysis;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx apply-fills</c> -- copies hand-maintained Tier-2 fills into the generated projects
/// and reports exactly what is still outstanding.
///
/// A "fill" is a second part of a generated <c>partial</c> transform class, implementing the
/// <c>Fill_&lt;Column&gt;</c> seams <c>ssisx generate --seams</c> emitted for columns produced by a
/// Script Component. It is human work product -- authored by a person, usually with AI help from
/// the SCRIPT-COLUMN work packet -- so this command only ever READS <c>fills/</c>, never writes to
/// it, exactly like <c>conformance</c>'s claims files. Fills are copied INTO
/// <c>generate/&lt;Package&gt;/Fills/</c> (their own folder, not mixed in with <c>Mapping/</c>) so a
/// reviewer can tell at a glance which files in the project are not machine-generated.
///
/// Validation is the real point, not the copy. A fill implementing a <c>Fill_*</c> that no gap
/// asked for is reported as ORPHANED and deliberately NOT copied -- an orphan usually means the
/// package changed and a seam disappeared, and copying it anyway would only produce a confusing
/// "no defining declaration" compile error instead of a clear message here. Seams with no fill at
/// all are reported too: they are the CS8795 errors the next build will produce.
/// </summary>
internal static class ApplyFillsCommand
{
    /// <summary>Matches an implementing part of a seam, e.g.
    /// <c>private partial string Fill_FullName(StagingCustomersCsvRow row, in RowContext ctx) =&gt; ...</c>.
    /// Deliberately loose about the return type (it can be nullable, generic, qualified) and about
    /// modifier order -- the compiler is the authority on whether the part actually matches its
    /// declaration; all this needs is the method NAME, to tell a real fill from an orphan.</summary>
    private static readonly Regex FillMethodPattern =
        new(@"partial\s+[^\s(]+\s+(Fill_\w+)\s*\(", RegexOptions.Compiled);

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
            Console.Error.WriteLine("error: apply-fills requires --out <dir> (the same --out 'ssisx generate' wrote).");
            return 2;
        }

        outDir = Path.GetFullPath(outDir);
        var gapsPath = Path.Combine(outDir, "gaps.json");
        if (!File.Exists(gapsPath))
        {
            Console.Error.WriteLine($"error: {gapsPath} not found -- run 'ssisx generate --out {outDir} --seams' first.");
            return 2;
        }

        List<GapSpec>? gaps;
        try
        {
            gaps = JsonSerializer.Deserialize<List<GapSpec>>(File.ReadAllText(gapsPath));
        }
        catch (JsonException ex)
        {
            // A malformed index is a hard error, not something to work around: silently treating
            // every seam as unfilled would be indistinguishable from a genuinely empty gap list.
            Console.Error.WriteLine($"error: {gapsPath} is not valid gaps.json ({ex.Message}).");
            return 2;
        }

        if (gaps is null)
        {
            Console.Error.WriteLine($"error: {gapsPath} deserialized to nothing.");
            return 2;
        }

        // Expected seams, per package: one per ScriptComponentColumn gap. GapSpec.Location is
        // "{Entity}.{Column}", and TransformEmitter names the seam Fill_{Column} -- so the column
        // is the part after the last '.'.
        var expected = new Dictionary<string, Dictionary<string, GapSpec>>(StringComparer.Ordinal);
        foreach (var gap in gaps.Where(g => g.Kind == GapKind.ScriptComponentColumn))
        {
            var column = gap.Location[(gap.Location.LastIndexOf('.') + 1)..];
            if (column.Length == 0) continue;
            if (!expected.TryGetValue(gap.Package, out var byMethod))
                expected[gap.Package] = byMethod = new Dictionary<string, GapSpec>(StringComparer.Ordinal);
            byMethod[$"Fill_{column}"] = gap;
        }

        fillsDir = Path.GetFullPath(fillsDir ?? Path.Combine(outDir, "fills"));
        var generateDir = Path.Combine(outDir, "generate");

        var applied = new List<string>();
        var orphanedFiles = new List<string>();
        var orphanedMethods = new List<string>();
        var unknownPackages = new List<string>();
        var filledMethods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        if (Directory.Exists(fillsDir))
        {
            foreach (var packageDir in Directory.EnumerateDirectories(fillsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var packageName = Path.GetFileName(packageDir);
                if (!Directory.Exists(Path.Combine(generateDir, packageName)))
                {
                    unknownPackages.Add(packageName);
                    continue;
                }

                foreach (var fillFile in Directory.EnumerateFiles(packageDir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var content = File.ReadAllText(fillFile);
                    var methods = FillMethodPattern.Matches(content).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
                    var known = expected.TryGetValue(packageName, out var byMethod) ? byMethod : [];
                    var recognized = methods.Where(known.ContainsKey).ToList();
                    var unrecognized = methods.Where(m => !known.ContainsKey(m)).ToList();

                    foreach (var method in unrecognized)
                        orphanedMethods.Add($"{packageName}/{Path.GetFileName(fillFile)}: {method}");

                    if (recognized.Count == 0)
                    {
                        // Nothing in this file answers a real seam. Skipping it keeps the failure
                        // legible here rather than as a compile error two steps later.
                        orphanedFiles.Add($"{packageName}/{Path.GetFileName(fillFile)}");
                        continue;
                    }

                    var targetDir = Path.Combine(generateDir, packageName, "Fills");
                    Directory.CreateDirectory(targetDir);
                    File.Copy(fillFile, Path.Combine(targetDir, Path.GetFileName(fillFile)), overwrite: true);
                    applied.Add($"{packageName}/Fills/{Path.GetFileName(fillFile)} ({string.Join(", ", recognized)})");

                    if (!filledMethods.TryGetValue(packageName, out var set))
                        filledMethods[packageName] = set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var method in recognized) set.Add(method);
                }
            }
        }

        var stillUnfilled = new List<string>();
        foreach (var (packageName, byMethod) in expected.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            filledMethods.TryGetValue(packageName, out var filled);
            foreach (var (method, gap) in byMethod.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (filled?.Contains(method) == true) continue;
                stillUnfilled.Add($"{gap.GapId}  ->  {method}");
            }
        }

        Report(fillsDir, applied, stillUnfilled, orphanedFiles, orphanedMethods, unknownPackages);

        if (orphanedFiles.Count > 0 || orphanedMethods.Count > 0 || unknownPackages.Count > 0) return 1;
        return stillUnfilled.Count > 0 ? 3 : 0;
    }

    private static void Report(
        string fillsDir, List<string> applied, List<string> stillUnfilled,
        List<string> orphanedFiles, List<string> orphanedMethods, List<string> unknownPackages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"apply-fills: {applied.Count} fill file(s) applied from {fillsDir}");
        foreach (var line in applied) sb.AppendLine($"  + {line}");

        if (stillUnfilled.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{stillUnfilled.Count} seam(s) still unfilled -- the next build will report one CS8795 each:");
            foreach (var line in stillUnfilled) sb.AppendLine($"  ! {line}");
        }

        if (orphanedFiles.Count > 0 || orphanedMethods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Orphaned -- implements a Fill_* no gap asked for, so NOT copied. Usually the package");
            sb.AppendLine("changed and that seam is gone; re-read the current work packet before reusing it.");
            foreach (var line in orphanedMethods) sb.AppendLine($"  ? {line}");
            foreach (var line in orphanedFiles) sb.AppendLine($"  ? {line} (skipped entirely -- nothing in it matched a seam)");
        }

        if (unknownPackages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Fills for a package that was not generated into --out:");
            foreach (var line in unknownPackages) sb.AppendLine($"  ? {line}");
        }

        if (applied.Count == 0 && stillUnfilled.Count == 0 && orphanedFiles.Count == 0 && orphanedMethods.Count == 0)
            sb.AppendLine("  (no Tier-2 seams in this --out, and no fills to apply)");

        Console.Write(sb.ToString());
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value");
        return args[++i];
    }
}
