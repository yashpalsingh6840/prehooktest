using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;

namespace Ssis.Extract.Tests;

/// <summary>
/// Unit tests for <see cref="PackageDiffer"/> (plan §6.2's <c>ssisx diff</c>). Uses the PoC's
/// real packages as the "left" side and byte-edited copies as the "right", because the
/// property that matters -- that a *semantic* change is reported and an incidental byte
/// change is not -- can only be demonstrated on real package XML.
/// </summary>
public class PackageDifferTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;
    private static readonly string FixturesDir =
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFilePath())!, "..", "..", "..", "..", "SSIS"));

    private static string LoadEmployeesPath => Path.Combine(FixturesDir, "LoadEmployees.dtsx");

    /// <summary>Writes a temp copy of the PoC package with <paramref name="find"/> replaced, so the diff runs against genuinely different bytes rather than a hand-built model.</summary>
    private static string WriteEditedCopy(string find, string replace)
    {
        var text = File.ReadAllText(LoadEmployeesPath);
        Assert.Contains(find, text); // guard: a silently non-matching edit would make the test vacuously pass
        var path = Path.Combine(Path.GetTempPath(), $"ssisx-difftest-{Guid.NewGuid():N}.dtsx");
        File.WriteAllText(path, text.Replace(find, replace));
        return path;
    }

    [Fact]
    public void SamePackageAgainstItself_IsIdentical()
    {
        var left = DtsxPackageReader.Read(LoadEmployeesPath, noRedact: false);
        var right = DtsxPackageReader.Read(LoadEmployeesPath, noRedact: false);

        var diff = PackageDiffer.Diff(left, right);

        Assert.True(diff.Identical);
        Assert.Empty(diff.Differences);
    }

    [Fact]
    public void ChangedSqlStatement_IsReportedAsAnExecutableChange()
    {
        var editedPath = WriteEditedCopy("TRUNCATE TABLE dbo.Employee;", "DELETE FROM dbo.Employee;");
        try
        {
            var left = DtsxPackageReader.Read(LoadEmployeesPath, noRedact: false);
            var right = DtsxPackageReader.Read(editedPath, noRedact: false);

            var diff = PackageDiffer.Diff(left, right);

            Assert.False(diff.Identical);
            var entry = Assert.Single(diff.Differences, d => d.Area == "Executables");
            Assert.Equal("Changed", entry.Change);
            Assert.Contains("TRUNCATE", entry.LeftValue);
            Assert.Contains("DELETE", entry.RightValue);
        }
        finally
        {
            File.Delete(editedPath);
        }
    }

    [Fact]
    public void ChangedDerivedColumnExpression_IsReportedAsAPipelineChange()
    {
        // The highest-value drift case: same components, same columns, different
        // transformation -- invisible to a component/column count comparison.
        var editedPath = WriteEditedCopy(
            "(DT_WSTR,101)(FirstName + \" \" + LastName)",
            "(DT_WSTR,101)(LastName + \", \" + FirstName)");
        try
        {
            var left = DtsxPackageReader.Read(LoadEmployeesPath, noRedact: false);
            var right = DtsxPackageReader.Read(editedPath, noRedact: false);

            var diff = PackageDiffer.Diff(left, right);

            var entry = Assert.Single(diff.Differences, d => d.Area == "Pipeline");
            Assert.Equal("Changed", entry.Change);
            Assert.Contains("DER_MergeColumns", entry.Identity);
        }
        finally
        {
            File.Delete(editedPath);
        }
    }

    [Fact]
    public void ByteChangeWithNoSemanticMeaning_ProducesNoDifferences()
    {
        // A designer layout coordinate (inside DesignTimeProperties -- pure GUI, which this
        // extractor deliberately never models) is the purest form of "the bytes changed and
        // nothing about the package did". This is the case that would flood a text diff with
        // noise on every save, and is exactly why 'ssisx diff' is semantic instead.
        var editedPath = WriteEditedCopy("Size=\"185,42\"", "Size=\"186,42\"");
        try
        {
            var left = DtsxPackageReader.Read(LoadEmployeesPath, noRedact: false);
            var right = DtsxPackageReader.Read(editedPath, noRedact: false);

            var diff = PackageDiffer.Diff(left, right);

            Assert.False(diff.Identical); // bytes differ
            Assert.Empty(diff.Differences); // ...but nothing semantic changed
        }
        finally
        {
            File.Delete(editedPath);
        }
    }
}
