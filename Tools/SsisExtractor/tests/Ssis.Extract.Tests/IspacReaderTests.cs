using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;

namespace Ssis.Extract.Tests;

/// <summary>
/// Tests for <see cref="IspacContents"/> against the PoC's own built <c>.ispac</c>, which is
/// a real deployed-project artifact rather than a fabricated zip.
///
/// <b>These skip themselves when the .ispac is absent</b> -- it's a build output
/// (<c>SSIS/bin/Development/</c>), not source, so it won't exist on a fresh clone until
/// someone builds the SSIS project (which needs Windows + SSDT, per CLAUDE.md). Failing here
/// on a clean checkout would be a false alarm about the extractor.
/// </summary>
public class IspacReaderTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static readonly string IspacPath = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFilePath())!, "..", "..", "..", "..", "SSIS", "bin", "Development", "SSIS.ispac"));

    /// <summary>
    /// Deliberately does NOT pin an exact package count. It used to assert exactly 2 and had
    /// been failing on committed state ever since the SSIS project legitimately grew to four
    /// packages (ScriptTest.dtsx and SyntheticParallelShapes.dtsx, both intentional additions --
    /// the latter lives in SSIS/ specifically so it appears in SSDT's Solution Explorer and can
    /// be built and executed). An exact count makes this test fail every time someone adds a
    /// package to the PoC project, which is the one thing that project exists to allow, and the
    /// failure says nothing about the .ispac reader under test.
    ///
    /// What matters, and what is asserted: the reader opens a REAL deployed-project artifact,
    /// every path it hands back exists, and the two packages this PoC is actually about are in
    /// there.
    /// </summary>
    [Fact]
    public void OpensBuiltIspacAndFindsThePoCPackages()
    {
        if (!File.Exists(IspacPath)) return; // see class doc comment

        using var contents = IspacContents.Open(IspacPath);

        Assert.NotEmpty(contents.DtsxPaths);
        Assert.All(contents.DtsxPaths, p => Assert.True(File.Exists(p)));
        Assert.Contains(contents.DtsxPaths, p => Path.GetFileName(p) == "LoadEmployees.dtsx");
        Assert.Contains(contents.DtsxPaths, p => Path.GetFileName(p) == "LoadReferenceData.dtsx");
    }

    [Fact]
    public void ExtractedPackagesParseIdenticallyToTheirRepoSources()
    {
        if (!File.Exists(IspacPath)) return;

        using var contents = IspacContents.Open(IspacPath);
        var deployed = contents.DtsxPaths.First(p => Path.GetFileName(p) == "LoadEmployees.dtsx");

        var package = DtsxPackageReader.Read(deployed, noRedact: false);

        // Same structural facts as the repo copy -- the build rewrites bytes, not meaning.
        Assert.Equal("LoadEmployees", package.ObjectName);
        Assert.Equal(100, package.Coverage.CoveragePercent);
        Assert.Contains(PackageTree.AllExecutables(package), e => e.DataFlowTask is not null);
    }

    [Fact]
    public void TempDirectoryIsCleanedUpOnDispose()
    {
        if (!File.Exists(IspacPath)) return;

        string extractedPath;
        using (var contents = IspacContents.Open(IspacPath))
        {
            extractedPath = contents.DtsxPaths[0];
            Assert.True(File.Exists(extractedPath));
        }

        // A leaked temp directory per run would matter on a 50-package portfolio.
        Assert.False(File.Exists(extractedPath));
    }

    [Fact]
    public void NonZipFile_FailsWithAMessageThatExplainsWhatAnIspacIs()
    {
        var notAZip = Path.Combine(Path.GetTempPath(), $"ssisx-notazip-{Guid.NewGuid():N}.ispac");
        File.WriteAllText(notAZip, "this is not a zip archive");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => IspacContents.Open(notAZip));
            Assert.Contains("zip", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(notAZip);
        }
    }
}
