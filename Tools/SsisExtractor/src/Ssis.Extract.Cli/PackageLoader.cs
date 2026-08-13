using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Project;

namespace Ssis.Extract.Cli;

/// <summary>
/// Resolves an <c>--input</c> value into loaded packages, shared by every command
/// (<c>extract</c>, <c>graph</c>, <c>report</c>, <c>diff</c>). Factored out in slice 5:
/// three commands had grown their own near-identical copy of this, and <c>.ispac</c>
/// support needed to land in all of them at once -- exactly the drift the
/// <c>usp_StartPackage</c>/<c>usp_StartRun</c> split in this repo's own control framework
/// was meant to prevent (see CLAUDE.md), applied here.
///
/// Accepts: a <c>.dtsx</c>, a <c>.dtproj</c> (+ its packages), an <c>.ispac</c> (a zip --
/// CLAUDE.md trap 1), or a directory containing one or more <c>.dtproj</c>/<c>.ispac</c>.
/// </summary>
internal static class PackageLoader
{
    internal sealed class LoadResult : IDisposable
    {
        public List<PackageSpec> Packages { get; } = [];
        public List<(string DtprojPath, ProjectSpec Project)> Projects { get; } = [];

        /// <summary>Temp directories from extracted <c>.ispac</c> archives, cleaned up on dispose -- callers must dispose (or the extracted packages' <c>SourceDtsxPath</c> values would outlive the files they point at).</summary>
        internal List<IspacContents> OpenArchives { get; } = [];

        public void Dispose()
        {
            foreach (var archive in OpenArchives)
            {
                archive.Dispose();
            }
        }
    }

    public static LoadResult Load(string input, bool noRedact, bool recursive)
    {
        var result = new LoadResult();
        try
        {
            LoadInto(result, input, noRedact, recursive);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static void LoadInto(LoadResult result, string input, bool noRedact, bool recursive)
    {
        if (File.Exists(input) && input.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase))
        {
            result.Packages.Add(DtsxPackageReader.Read(input, noRedact));
            return;
        }

        if (File.Exists(input) && input.EndsWith(".dtproj", StringComparison.OrdinalIgnoreCase))
        {
            LoadProject(result, input, noRedact);
            return;
        }

        if (File.Exists(input) && input.EndsWith(".ispac", StringComparison.OrdinalIgnoreCase))
        {
            LoadIspac(result, input, noRedact);
            return;
        }

        if (Directory.Exists(input))
        {
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var dtprojFiles = Directory.EnumerateFiles(input, "*.dtproj", searchOption).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var ispacFiles = Directory.EnumerateFiles(input, "*.ispac", searchOption).OrderBy(p => p, StringComparer.Ordinal).ToList();

            if (dtprojFiles.Count == 0 && ispacFiles.Count == 0)
            {
                var hint = recursive ? "" : " (pass --recursive to search subdirectories)";
                throw new FileNotFoundException($"no .dtproj or .ispac found under {input}{hint}");
            }

            foreach (var dtproj in dtprojFiles)
            {
                LoadProject(result, dtproj, noRedact);
            }
            foreach (var ispac in ispacFiles)
            {
                LoadIspac(result, ispac, noRedact);
            }
            return;
        }

        throw new FileNotFoundException($"--input must be a .dtsx, .dtproj, or .ispac file, or a directory: {input}");
    }

    private static void LoadProject(LoadResult result, string dtprojPath, bool noRedact)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(dtprojPath))!;
        var projectParamsPath = Path.Combine(projectDir, "Project.params");
        var project = DtprojReader.Read(dtprojPath, File.Exists(projectParamsPath) ? projectParamsPath : null);
        result.Projects.Add((dtprojPath, project));

        foreach (var pkgEntry in project.Packages)
        {
            var dtsxPath = Path.Combine(projectDir, pkgEntry.Name);
            if (!File.Exists(dtsxPath))
            {
                Console.Error.WriteLine($"warning: {pkgEntry.Name} listed in {Path.GetFileName(dtprojPath)} but not found at {dtsxPath} -- skipped");
                continue;
            }
            result.Packages.Add(DtsxPackageReader.Read(dtsxPath, noRedact));
        }
    }

    private static void LoadIspac(LoadResult result, string ispacPath, bool noRedact)
    {
        var contents = IspacContents.Open(ispacPath);
        result.OpenArchives.Add(contents);

        // An .ispac carries a generated @Project.manifest rather than the authoring-time
        // .dtproj, so there is no ProjectSpec to record here -- packages only. That's why
        // 'extract' against an .ispac writes no project.spec.json; see docs/spec-schema.md.
        foreach (var dtsxPath in contents.DtsxPaths)
        {
            result.Packages.Add(DtsxPackageReader.Read(dtsxPath, noRedact));
        }
    }
}
