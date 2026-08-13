using System.IO.Compression;

namespace Ssis.Extract.Dtsx;

/// <summary>
/// Reads a deployed <c>.ispac</c> (plan §6.2: "`.ispac` is just a zip -- CLAUDE.md trap 1 --
/// so `ZipArchive` reads it"). Extracts the archive to a temp directory so the existing
/// path-based readers work unchanged, rather than duplicating every reader against a stream
/// API. Windows' own <c>Expand-Archive</c> rejects the <c>.ispac</c> extension (trap 1);
/// <see cref="ZipFile"/> does not care about the extension at all, only the content, which
/// is why this needs no rename dance.
///
/// This is what makes source-vs-deployed drift detection possible offline: extract the repo
/// copy, extract the <c>.ispac</c>, and <c>ssisx diff</c> the two -- no SSISDB access
/// required, which matters given this engagement has none (plan §11 decision 4).
/// </summary>
public sealed class IspacContents : IDisposable
{
    private readonly string _tempDir;

    private IspacContents(string tempDir, string? dtprojPath, List<string> dtsxPaths, string? projectParamsPath)
    {
        _tempDir = tempDir;
        DtprojPath = dtprojPath;
        DtsxPaths = dtsxPaths;
        ProjectParamsPath = projectParamsPath;
    }

    /// <summary>
    /// Always null in practice: an <c>.ispac</c> contains a generated
    /// <c>@Project.manifest</c>, not the authoring-time <c>.dtproj</c>. Kept as a field so
    /// callers can branch uniformly on "did this input carry a project file", and so a
    /// future slice can map the manifest onto <c>ProjectSpec</c> without changing this
    /// type's shape.
    /// </summary>
    public string? DtprojPath { get; }

    public List<string> DtsxPaths { get; }
    public string? ProjectParamsPath { get; }

    public static IspacContents Open(string ispacPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ssisx-ispac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            ZipFile.ExtractToDirectory(ispacPath, tempDir);
        }
        catch (InvalidDataException ex)
        {
            Directory.Delete(tempDir, recursive: true);
            throw new InvalidDataException($"{ispacPath} is not a readable zip archive -- an .ispac is a zip, so this usually means the file is corrupt or is not actually an .ispac: {ex.Message}", ex);
        }

        var dtsxPaths = Directory.EnumerateFiles(tempDir, "*.dtsx", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var dtprojPath = Directory.EnumerateFiles(tempDir, "*.dtproj", SearchOption.AllDirectories).FirstOrDefault();
        var projectParamsPath = Directory.EnumerateFiles(tempDir, "Project.params", SearchOption.AllDirectories).FirstOrDefault();

        if (dtsxPaths.Count == 0)
        {
            Directory.Delete(tempDir, recursive: true);
            throw new InvalidDataException($"{ispacPath} contains no .dtsx files -- readable as a zip, but not an SSIS project deployment file.");
        }

        return new IspacContents(tempDir, dtprojPath, dtsxPaths, projectParamsPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing an otherwise-successful
            // extraction over; the OS will reclaim it.
        }
    }
}
