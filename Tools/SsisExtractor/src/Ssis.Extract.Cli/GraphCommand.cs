using Ssis.Extract.Cli.Rendering;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx graph</c> -- writes a Mermaid (<c>.mmd</c>) and Graphviz DOT (<c>.dot</c>) file
/// per Data Flow Task's column-level lineage (plan §5.1), one pair per task under
/// <c>&lt;out&gt;/lineage/</c>. Reuses the same package readers as <c>extract</c> but does
/// not require having run <c>extract</c> first -- lineage is derived straight from the
/// parsed pipeline in memory (<see cref="LineageBuilder"/>), same as it is inside
/// <c>DtsxPackageReader.Read</c> itself.
/// </summary>
internal static class GraphCommand
{
    public static int Run(string[] args)
    {
        string? input = null;
        string? outDir = null;
        var recursive = false;
        var packageNames = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            try
            {
                switch (args[i])
                {
                    case "--input": input = RequireValue(args, ref i, "--input"); break;
                    case "--out": outDir = RequireValue(args, ref i, "--out"); break;
                    case "--recursive": recursive = true; break;
                    case "--package":
                        var pkgArg = RequireValue(args, ref i, "--package");
                        packageNames.AddRange(pkgArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                        break;
                    default:
                        Console.Error.WriteLine($"error: unknown graph option '{args[i]}'");
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

        try
        {
            using var loaded = PackageLoader.Load(input, noRedact: false, recursive, packageNames);
            var packages = loaded.Packages;

            if (packageNames.Count > 0 && packages.Count == 0)
            {
                Console.Error.WriteLine("error: --package matched no packages under this --input -- nothing to graph.");
                return 2;
            }

            var lineageDir = Path.Combine(outDir, "lineage");
            Directory.CreateDirectory(lineageDir);

            var written = 0;
            foreach (var pkg in packages)
            {
                foreach (var (path, dft) in FindDataFlowTasks(pkg.Executables, ""))
                {
                    var title = $"{pkg.ObjectName} / {path}";
                    var baseName = SanitizeFileName($"{pkg.ObjectName}__{path}");
                    var graph = LineageGraphModel.Build(dft.Pipeline, dft.Lineage);

                    var mmdPath = Path.Combine(lineageDir, $"{baseName}.mmd");
                    File.WriteAllText(mmdPath, MermaidRenderer.Render(graph, title));
                    Console.WriteLine($"wrote {mmdPath}");

                    var dotPath = Path.Combine(lineageDir, $"{baseName}.dot");
                    File.WriteAllText(dotPath, DotRenderer.Render(graph, title));
                    Console.WriteLine($"wrote {dotPath}");

                    written++;
                }
            }

            Console.WriteLine($"wrote lineage diagrams for {written} data flow task(s) from {packages.Count} package(s) -> {lineageDir}");
            if (written == 0)
            {
                Console.WriteLine("(no Data Flow Tasks found -- nothing to diagram)");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    /// <summary>Walks the control-flow tree (children, recursively -- event handlers deliberately excluded: plan §4.6 files them as a later concern, and neither PoC package has one to validate against) collecting every populated <see cref="ExecutableSpec.DataFlowTask"/>, tagged with a "/"-joined path of ObjectNames for a readable file name.</summary>
    private static IEnumerable<(string Path, DataFlowTaskPayload Dft)> FindDataFlowTasks(List<ExecutableSpec> executables, string prefix)
    {
        foreach (var ex in executables)
        {
            var path = string.IsNullOrEmpty(prefix) ? (ex.ObjectName ?? ex.RefId) : $"{prefix}/{ex.ObjectName ?? ex.RefId}";
            if (ex.DataFlowTask is not null)
            {
                yield return (path, ex.DataFlowTask);
            }
            foreach (var nested in FindDataFlowTasks(ex.Children, path))
            {
                yield return nested;
            }
        }
    }

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
