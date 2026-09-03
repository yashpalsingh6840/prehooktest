using System.Text;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Serialization;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx diff</c> -- semantic diff between two versions of the same package (plan §6.2).
/// Both sides accept a <c>.dtsx</c> or an <c>.ispac</c>, which is the whole point: the
/// intended use is diffing a repo's source against a deployed <c>.ispac</c> to answer
/// "which packages can I not trust the source of" -- the plan's own framing of why this
/// matters on day one of an engagement.
///
/// The plan's CLI sketch shows <c>--left a.spec.json --right b.spec.json</c>; this takes
/// package files directly instead, because requiring two prior <c>extract</c> runs just to
/// compare two packages is friction with no benefit (the diff is computed from the same
/// typed model either way). Exit code 1 on <i>semantic</i> differences (never on the bytes
/// merely differing), so it works as a CI drift gate without firing on every rebuild.
/// </summary>
internal static class DiffCommand
{
    public static int Run(string[] args)
    {
        string? left = null;
        string? right = null;
        string? outPath = null;
        string? packageName = null;

        for (var i = 0; i < args.Length; i++)
        {
            try
            {
                switch (args[i])
                {
                    case "--left": left = RequireValue(args, ref i, "--left"); break;
                    case "--right": right = RequireValue(args, ref i, "--right"); break;
                    case "--out": outPath = RequireValue(args, ref i, "--out"); break;
                    case "--package": packageName = RequireValue(args, ref i, "--package"); break;
                    default:
                        Console.Error.WriteLine($"error: unknown diff option '{args[i]}'");
                        return 2;
                }
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 2;
            }
        }

        if (left is null || right is null)
        {
            Console.Error.WriteLine("error: --left and --right are required. Run 'ssisx --help'.");
            return 2;
        }
        if (!Path.Exists(left)) { Console.Error.WriteLine($"error: --left not found: {left}"); return 2; }
        if (!Path.Exists(right)) { Console.Error.WriteLine($"error: --right not found: {right}"); return 2; }

        List<PackageDiffSpec> diffs;
        try
        {
            using var leftLoaded = PackageLoader.Load(Path.GetFullPath(left), noRedact: false, recursive: false);
            using var rightLoaded = PackageLoader.Load(Path.GetFullPath(right), noRedact: false, recursive: false);

            var leftPackages = Filter(leftLoaded.Packages, packageName);
            var rightPackages = Filter(rightLoaded.Packages, packageName);

            if (leftPackages.Count == 0 || rightPackages.Count == 0)
            {
                Console.Error.WriteLine(packageName is null
                    ? "error: one or both sides contain no packages."
                    : $"error: no package named '{packageName}' on one or both sides.");
                return 2;
            }

            diffs = PairUp(leftPackages, rightPackages);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        var report = RenderMarkdown(diffs);
        if (outPath is not null)
        {
            var full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, report);
            StableJsonWriter.WriteToFile(diffs, Path.ChangeExtension(full, ".json"));
            Console.WriteLine($"wrote {full}");
            Console.WriteLine($"wrote {Path.ChangeExtension(full, ".json")}");
        }
        else
        {
            Console.Write(report);
        }

        // Exit on SEMANTIC differences, not on the SHA-256 differing. A build rewrites the
        // .dtsx (version stamps, designer layout) without changing what the package does, so
        // gating CI on bytes would cry wolf on every single build -- which would defeat the
        // reason this is a semantic diff at all. Verified against this PoC: its repo .dtsx
        // and the .ispac built from it differ byte-for-byte with zero semantic differences.
        var anySemanticDifferences = diffs.Any(d => d.Differences.Count > 0);
        return anySemanticDifferences ? 1 : 0;
    }

    private static List<PackageSpec> Filter(List<PackageSpec> packages, string? packageName) =>
        packageName is null
            ? packages
            : packages.Where(p => p.ObjectName.Equals(packageName, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Matches packages across the two sides by name. A single package on each side is
    /// paired regardless of name (diffing a renamed package is a legitimate thing to want,
    /// and the name difference shows up as its own <c>PackageProperties</c> entry); with
    /// more than one, only same-named pairs are compared, and unmatched packages on either
    /// side are reported as added/removed at the portfolio level rather than silently dropped.
    /// </summary>
    private static List<PackageDiffSpec> PairUp(List<PackageSpec> left, List<PackageSpec> right)
    {
        if (left.Count == 1 && right.Count == 1)
        {
            return [PackageDiffer.Diff(left[0], right[0])];
        }

        var diffs = new List<PackageDiffSpec>();
        var rightByName = right.ToDictionary(p => p.ObjectName, StringComparer.OrdinalIgnoreCase);

        foreach (var l in left.OrderBy(p => p.ObjectName, StringComparer.Ordinal))
        {
            if (rightByName.TryGetValue(l.ObjectName, out var r))
            {
                diffs.Add(PackageDiffer.Diff(l, r));
            }
            else
            {
                diffs.Add(MissingSide(l.ObjectName, l.Sha256, presentOnLeft: true));
            }
        }

        var leftNames = left.Select(p => p.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in right.Where(p => !leftNames.Contains(p.ObjectName)).OrderBy(p => p.ObjectName, StringComparer.Ordinal))
        {
            diffs.Add(MissingSide(r.ObjectName, r.Sha256, presentOnLeft: false));
        }

        return diffs;
    }

    private static PackageDiffSpec MissingSide(string name, string sha, bool presentOnLeft) => new()
    {
        LeftPackageName = presentOnLeft ? name : "(absent)",
        RightPackageName = presentOnLeft ? "(absent)" : name,
        LeftSha256 = presentOnLeft ? sha : "",
        RightSha256 = presentOnLeft ? "" : sha,
        Identical = false,
        Differences =
        [
            new PackageDiffEntry
            {
                Area = "PackageProperties",
                Change = presentOnLeft ? "Removed" : "Added",
                Identity = name,
                LeftValue = presentOnLeft ? name : null,
                RightValue = presentOnLeft ? null : name,
            },
        ],
    };

    private static string RenderMarkdown(List<PackageDiffSpec> diffs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Package diff");
        sb.AppendLine();

        foreach (var d in diffs)
        {
            sb.AppendLine($"## {d.LeftPackageName} vs {d.RightPackageName}");
            sb.AppendLine();
            sb.AppendLine($"- left SHA-256: `{d.LeftSha256}`");
            sb.AppendLine($"- right SHA-256: `{d.RightSha256}`");
            sb.AppendLine();

            if (d.Identical)
            {
                sb.AppendLine("**Byte-identical.** No drift.");
                sb.AppendLine();
                continue;
            }

            if (d.Differences.Count == 0)
            {
                sb.AppendLine("Files differ byte-for-byte, but no *semantic* differences were found in the modeled areas -- typically a designer-layout or version-stamp change only. That distinction is the whole reason this is a semantic diff rather than a text diff.");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"{d.Differences.Count} semantic difference(s).");
            sb.AppendLine();
            foreach (var group in d.Differences.GroupBy(x => x.Area).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"### {group.Key}");
                sb.AppendLine();
                sb.AppendLine("| Change | Identity | Left | Right |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var e in group.OrderBy(x => x.Identity, StringComparer.Ordinal))
                {
                    sb.AppendLine($"| {e.Change} | {Escape(e.Identity)} | {Escape(e.LeftValue ?? "-")} | {Escape(e.RightValue ?? "-")} |");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

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
