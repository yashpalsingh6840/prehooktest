using System.Text.Json;
using System.Text.Json.Serialization;
using Ssis.Extract.Dtsx;
using Svk.Core;

namespace Svk.Cli;

public static class WalkthroughCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Run(string[] args)
    {
        string? spec = null, outDir = null, claimsDir = null;
        var packages = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--spec": spec = CliArgs.Require(args, ref i); break;
                case "--out": outDir = CliArgs.Require(args, ref i); break;
                case "--claims": claimsDir = CliArgs.Require(args, ref i); break;
                case "--package": packages.AddRange(CliArgs.SplitPackages(CliArgs.Require(args, ref i))); break;
                case "--help": PrintHelp(); return 0;
                default: Console.Error.WriteLine($"error: unknown option '{args[i]}'."); return 2;
            }
        }

        if (spec is null || outDir is null)
        {
            Console.Error.WriteLine("error: --spec and --out are required. Run 'svk walkthrough --help'.");
            return 2;
        }
        claimsDir ??= Path.Combine(outDir, "walkthrough", "claims");

        List<Ssis.Extract.Model.Package.PackageSpec> loaded;
        try
        {
            loaded = PackageSpecLoader.Load(spec, packages);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        if (loaded.Count == 0)
        {
            Console.Error.WriteLine("error: no packages loaded -- check --spec and --package.");
            return 2;
        }

        foreach (var pkg in loaded)
        {
            var steps = ExecutionOrderBuilder.Build(pkg);
            var dataFlowRules = ConformanceRulesBuilder.Build(pkg).Where(r => r.Category == "DataFlow").ToList();

            var claimsPath = Path.Combine(claimsDir, $"{pkg.ObjectName}.claims.json");
            var claims = LoadClaims(claimsPath);
            foreach (var rule in dataFlowRules)
            {
                if (!claims.ContainsKey(rule.RuleId))
                {
                    claims[rule.RuleId] = new WalkthroughClaim { RuleId = rule.RuleId };
                }
            }
            if (!File.Exists(claimsPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(claimsPath)!);
                var ordered = claims.Values.OrderBy(c => c.RuleId, StringComparer.Ordinal).ToList();
                File.WriteAllText(claimsPath, JsonSerializer.Serialize(ordered, JsonOptions));
            }

            var md = WalkthroughBuilder.BuildMarkdown(pkg, steps, dataFlowRules, claims);
            var mdPath = Path.Combine(outDir, "walkthrough", $"{pkg.ObjectName}.walkthrough.md");
            Directory.CreateDirectory(Path.GetDirectoryName(mdPath)!);
            File.WriteAllText(mdPath, md);

            Console.WriteLine($"{pkg.ObjectName}: {steps.Count} step(s), {dataFlowRules.Count} data-flow obligation(s) -> {mdPath}");
        }

        return 0;
    }

    private static Dictionary<string, WalkthroughClaim> LoadClaims(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, WalkthroughClaim>(StringComparer.Ordinal);
        var list = JsonSerializer.Deserialize<List<WalkthroughClaim>>(File.ReadAllText(path), JsonOptions) ?? [];
        return list.ToDictionary(c => c.RuleId, StringComparer.Ordinal);
    }

    private static void PrintHelp() => Console.WriteLine("""
        svk walkthrough --spec <extract-out-dir> --out <dir> [--claims <dir>] [--package <name>[,<name>...]]

        One markdown document per package, in real SSIS execution order, calling out
        parallel-execution groups and linking each Data Flow component to its matching
        `ssisx conformance` rule ID. Regenerates the walkthrough .md on every run; the
        claims file (default <out>/walkthrough/claims/<Package>.claims.json) is written
        once, stubbed, and never overwritten again -- record Status/Reviewer/Note there.
        """);
}
