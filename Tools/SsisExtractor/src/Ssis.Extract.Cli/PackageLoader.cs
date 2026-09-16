using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Project;

namespace Ssis.Extract.Cli;

/// <summary>
/// Resolves an <c>--input</c> value into loaded packages, shared by every command
/// (<c>extract</c>, <c>graph</c>, <c>report</c>, <c>conformance</c>, <c>testgen</c>,
/// <c>diff</c>). Factored out in slice 5: three commands had grown their own near-identical
/// copy of this, and <c>.ispac</c> support needed to land in all of them at once -- exactly
/// the drift the <c>usp_StartPackage</c>/<c>usp_StartRun</c> split in this repo's own control
/// framework was meant to prevent (see CLAUDE.md), applied here.
///
/// Accepts: a <c>.dtsx</c>, a <c>.dtproj</c> (+ its packages), an <c>.ispac</c> (a zip --
/// CLAUDE.md trap 1), or a directory holding any mix of those, INCLUDING loose <c>.dtsx</c>
/// files with no <c>.dtproj</c> alongside them.
///
/// <para><b>Every unit is loaded in isolation and a failure is recorded, never thrown.</b>
/// This is not defensive tidiness -- it is the difference between a usable and a useless run
/// on a machine you do not control. Before this, all N packages were parsed inside one
/// try/catch at the call site, so a single malformed or unsupported package aborted the whole
/// invocation and wrote NOTHING for the other N-1 (verified empirically, not assumed: one
/// corrupt .dtsx among two projects produced "error: Unexpected end of file...", exit 2, and
/// an out dir that was never created). On a client site that gets sampled once, that turns a
/// completed survey into a wasted trip. A hard throw now survives only for "the --input
/// itself is unusable", where there is genuinely nothing to partially succeed at.</para>
/// </summary>
internal static class PackageLoader
{
    /// <summary>One input artifact that could not be read, kept so callers can report it alongside the results rather than in place of them.</summary>
    internal sealed record LoadFailure(string Path, string Kind, string Reason);

    internal sealed class LoadResult : IDisposable
    {
        public List<PackageSpec> Packages { get; } = [];
        public List<(string DtprojPath, ProjectSpec Project)> Projects { get; } = [];

        /// <summary>Artifacts that failed to load. Empty on a clean run; callers should surface it (and, for report, persist it) rather than silently returning partial results.</summary>
        public List<LoadFailure> Failures { get; } = [];

        /// <summary>Full paths of every .dtsx already ATTEMPTED -- successes and failures alike -- so the directory sweep neither re-reads a package it loaded through a .dtproj nor retries (and double-reports) one that just failed.</summary>
        internal HashSet<string> AttemptedDtsxPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// <paramref name="packageNames"/> is the "one package, or a handful, not all 73" knob every
    /// command shares (<c>--package</c>, repeatable). Filtering happens HERE, after the full
    /// directory sweep and duplicate-name resolution, rather than by resolving names to paths up
    /// front -- a package is matched by its real <c>ObjectName</c> (the .dtsx's own
    /// <c>DTS:ObjectName</c>, not necessarily its filename; the two occasionally differ, e.g. a
    /// renamed-on-disk copy) against whatever the directory sweep actually found, so the same
    /// duplicate-name/build-output/load-failure handling above applies uniformly whether or not a
    /// filter is in play. A requested name that matches nothing is recorded as a
    /// <see cref="LoadFailure"/> (kind <c>"package-not-found"</c>) rather than silently ignored --
    /// a typo'd package name must not look like "ran clean, zero gaps" for a package that was
    /// never actually touched. Deliberately does NOT filter <see cref="LoadResult.Projects"/> --
    /// a project's own manifest is cheap and small, and callers that write one file per project
    /// (extract's project.spec.json) get it regardless of which packages were asked for.
    /// </summary>
    public static LoadResult Load(string input, bool noRedact, bool recursive, IReadOnlyList<string>? packageNames = null)
    {
        var result = new LoadResult();
        try
        {
            LoadInto(result, input, noRedact, recursive);
            DropDuplicateNames(result);
            if (packageNames is { Count: > 0 })
            {
                FilterByName(result, packageNames);
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static void FilterByName(LoadResult result, IReadOnlyList<string> requestedNames)
    {
        var wanted = new HashSet<string>(requestedNames, StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var kept = new List<PackageSpec>(result.Packages.Count);
        foreach (var pkg in result.Packages)
        {
            if (wanted.Contains(pkg.ObjectName))
            {
                matched.Add(pkg.ObjectName);
                kept.Add(pkg);
            }
        }
        result.Packages.Clear();
        result.Packages.AddRange(kept);

        foreach (var name in wanted)
        {
            if (matched.Contains(name)) continue;
            result.Failures.Add(new LoadFailure(name, "package-not-found",
                $"--package '{name}' did not match any package's ObjectName found under the given --input (case-insensitive exact match against the .dtsx's own DTS:ObjectName -- run without --package to see every name this input actually contains)"));
            Console.Error.WriteLine($"warning: --package '{name}' matched no package under this --input -- skipped");
        }
    }

    /// <summary>
    /// Every downstream report is keyed by package ObjectName, so two packages sharing one
    /// name is not a cosmetic clash -- it crashed the whole run with an unhandled
    /// ArgumentException out of a dictionary build, losing the survey entirely. Found the
    /// obvious way: pointing --recursive at a source tree that also contained its own
    /// built .ispac loaded every package twice.
    ///
    /// <para>Build output is filtered out before this (see <see cref="IsBuildOutput"/>), which
    /// removes that cause. This is the backstop for the rest -- most plausibly the same
    /// package name reused across two different client projects. First one wins (projects
    /// load before loose files, and both are ordinal-sorted, so the choice is deterministic),
    /// and the loser is reported rather than silently dropped: an under-counted portfolio that
    /// looks complete is worse than a named omission.</para>
    /// </summary>
    private static void DropDuplicateNames(LoadResult result)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<PackageSpec>(result.Packages.Count);

        foreach (var pkg in result.Packages)
        {
            if (seen.TryGetValue(pkg.ObjectName, out var firstPath))
            {
                result.Failures.Add(new LoadFailure(pkg.SourceDtsxPath, "duplicate-name",
                    $"another package already loaded with ObjectName '{pkg.ObjectName}' (from {firstPath}); reports are keyed by package name, so this copy was skipped"));
                Console.Error.WriteLine($"warning: duplicate package name '{pkg.ObjectName}' -- kept {firstPath}, skipped {pkg.SourceDtsxPath}");
                continue;
            }
            seen[pkg.ObjectName] = pkg.SourceDtsxPath;
            kept.Add(pkg);
        }

        if (kept.Count == result.Packages.Count) return;
        result.Packages.Clear();
        result.Packages.AddRange(kept);
    }

    /// <summary>A recursive sweep of a source tree otherwise picks up that tree's OWN build output -- the .ispac under bin/ holds the same packages as the .dtsx beside the .dtproj, so every package loads twice. Excluded by path segment rather than by name so it works for .dtsx, .dtproj, and .ispac alike.</summary>
    private static bool IsBuildOutput(string path)
    {
        var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) || p.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static void LoadInto(LoadResult result, string input, bool noRedact, bool recursive)
    {
        if (File.Exists(input) && input.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase))
        {
            LoadDtsx(result, input, noRedact);
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
            var dtprojFiles = Enumerate(input, "*.dtproj", searchOption);
            var ispacFiles = Enumerate(input, "*.ispac", searchOption);
            var dtsxFiles = Enumerate(input, "*.dtsx", searchOption);

            if (dtprojFiles.Count == 0 && ispacFiles.Count == 0 && dtsxFiles.Count == 0)
            {
                var hint = recursive ? "" : " (pass --recursive to search subdirectories)";
                throw new FileNotFoundException($"no .dtproj, .ispac, or .dtsx found under {input}{hint}");
            }

            // Projects and .ispac archives first: they carry project-level context
            // (Project.params, the manifest) that a loose .dtsx cannot, so a package is
            // attributed to its project wherever one exists.
            foreach (var dtproj in dtprojFiles)
            {
                LoadProject(result, dtproj, noRedact);
            }
            foreach (var ispac in ispacFiles)
            {
                LoadIspac(result, ispac, noRedact);
            }

            // Then any .dtsx the sweep found that no .dtproj claimed. A folder of loose
            // packages -- how a .dtsx portfolio usually arrives when someone zips one up
            // without the project files -- is the whole reason this pass exists.
            foreach (var dtsx in dtsxFiles)
            {
                if (result.AttemptedDtsxPaths.Contains(Path.GetFullPath(dtsx))) continue;
                LoadDtsx(result, dtsx, noRedact);
            }
            return;
        }

        throw new FileNotFoundException($"--input must be a .dtsx, .dtproj, or .ispac file, or a directory containing them: {input}");
    }

    private static List<string> Enumerate(string dir, string pattern, SearchOption searchOption) =>
        Directory.EnumerateFiles(dir, pattern, searchOption)
                 .Where(p => !IsBuildOutput(p))
                 .OrderBy(p => p, StringComparer.Ordinal)
                 .ToList();

    /// <summary>Reads one package, recording rather than propagating a failure. Returns true if it loaded.</summary>
    private static bool LoadDtsx(LoadResult result, string dtsxPath, bool noRedact, IReadOnlyList<Ssis.Extract.Model.Shared.ConnectionManagerSpec>? projectConnectionManagers = null)
    {
        var fullPath = Path.GetFullPath(dtsxPath);
        // Marked BEFORE the read, not after: a failed package must still count as seen, or
        // the loose-.dtsx sweep retries every package a .dtproj already failed on and
        // reports each one twice.
        result.AttemptedDtsxPaths.Add(fullPath);
        try
        {
            result.Packages.Add(DtsxPackageReader.Read(dtsxPath, noRedact, projectConnectionManagers));
            return true;
        }
        catch (Exception ex)
        {
            result.Failures.Add(new LoadFailure(fullPath, "package", ex.Message));
            Console.Error.WriteLine($"warning: could not read {Path.GetFileName(dtsxPath)} -- skipped ({ex.Message})");
            return false;
        }
    }

    private static void LoadProject(LoadResult result, string dtprojPath, bool noRedact)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(dtprojPath))!;
        var projectParamsPath = Path.Combine(projectDir, "Project.params");

        ProjectSpec project;
        try
        {
            project = DtprojReader.Read(dtprojPath, File.Exists(projectParamsPath) ? projectParamsPath : null, noRedact);
        }
        catch (Exception ex)
        {
            // The .dtproj is only a manifest. If it is unreadable the packages beside it
            // usually are not, so fall back to sweeping the folder rather than losing them.
            result.Failures.Add(new LoadFailure(Path.GetFullPath(dtprojPath), "project", ex.Message));
            Console.Error.WriteLine($"warning: could not read {Path.GetFileName(dtprojPath)} ({ex.Message}) -- reading .dtsx files beside it instead");
            foreach (var dtsx in Enumerate(projectDir, "*.dtsx", SearchOption.TopDirectoryOnly))
            {
                if (!result.AttemptedDtsxPaths.Contains(Path.GetFullPath(dtsx))) LoadDtsx(result, dtsx, noRedact);
            }
            return;
        }

        result.Projects.Add((dtprojPath, project));

        foreach (var pkgEntry in project.Packages)
        {
            var dtsxPath = Path.Combine(projectDir, pkgEntry.Name);
            if (!File.Exists(dtsxPath))
            {
                result.Failures.Add(new LoadFailure(dtsxPath, "package", $"listed in {Path.GetFileName(dtprojPath)} but not found on disk"));
                Console.Error.WriteLine($"warning: {pkgEntry.Name} listed in {Path.GetFileName(dtprojPath)} but not found at {dtsxPath} -- skipped");
                continue;
            }
            LoadDtsx(result, dtsxPath, noRedact, project.ConnectionManagers);
        }
    }

    private static void LoadIspac(LoadResult result, string ispacPath, bool noRedact)
    {
        IspacContents contents;
        try
        {
            contents = IspacContents.Open(ispacPath);
        }
        catch (Exception ex)
        {
            result.Failures.Add(new LoadFailure(Path.GetFullPath(ispacPath), "ispac", ex.Message));
            Console.Error.WriteLine($"warning: could not open {Path.GetFileName(ispacPath)} -- skipped ({ex.Message})");
            return;
        }

        result.OpenArchives.Add(contents);

        // An .ispac carries a generated @Project.manifest rather than the authoring-time
        // .dtproj, so there is no ProjectSpec to record here -- packages only. That's why
        // 'extract' against an .ispac writes no project.spec.json; see docs/spec-schema.md.
        foreach (var dtsxPath in contents.DtsxPaths)
        {
            LoadDtsx(result, dtsxPath, noRedact);
        }
    }
}
