using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Serialization;

namespace Ssis.Extract.Tests;

/// <summary>
/// Golden-file tests (plan §7.1): assert the extractor's output for this PoC's own two
/// packages is byte-identical -- modulo a handful of environment-specific fields, see
/// <see cref="NormalizeVolatileFields"/> -- to a committed expected spec.json. Any parser
/// change that alters output shows up here as a reviewable diff against Golden/*.json.
///
/// Fixtures are the PoC's actual packages, referenced by relative path rather than
/// copied (plan §2.2: "This PoC's two packages are fixtures, referenced by path"). One
/// side effect worth knowing: if someone edits a PoC package's XML directly, this test
/// starts failing even though nothing about the extractor changed -- that's a feature,
/// not a bug (it's the same "unexpected drift" signal the whole tool exists to surface,
/// just now pointed at itself), but it does mean a golden-file diff needs a moment of
/// "did the fixture change, or did the parser?" before regenerating blindly.
///
/// To regenerate after an intentional model/output change:
///   SSISX_REGENERATE_GOLDEN=1 dotnet test tests/Ssis.Extract.Tests
/// then review the resulting git diff under Golden/ like any other code change.
/// </summary>
public class GoldenFileTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static readonly string TestsProjectDir = Path.GetDirectoryName(ThisFilePath())!;

    // Tools/SsisExtractor/tests/Ssis.Extract.Tests -> repo root -> SSIS/ (the PoC project)
    private static readonly string FixturesDir =
        Path.GetFullPath(Path.Combine(TestsProjectDir, "..", "..", "..", "..", "SSIS"));

    private static readonly string GoldenDir = Path.Combine(TestsProjectDir, "Golden");

    [Fact]
    public void ProjectSpec_MatchesGoldenFile()
    {
        var project = DtprojReader.Read(
            Path.Combine(FixturesDir, "SSIS.dtproj"),
            Path.Combine(FixturesDir, "Project.params"));

        AssertMatchesGolden(project, "project.spec.json");
    }

    [Theory]
    [InlineData("LoadEmployees.dtsx", "LoadEmployees.spec.json")]
    [InlineData("LoadReferenceData.dtsx", "LoadReferenceData.spec.json")]
    public void PackageSpec_MatchesGoldenFile(string dtsxFileName, string goldenFileName)
    {
        var package = DtsxPackageReader.Read(Path.Combine(FixturesDir, dtsxFileName), noRedact: false);
        AssertMatchesGolden(package, goldenFileName);
    }

    private static void AssertMatchesGolden<T>(T value, string goldenFileName)
    {
        var actual = NormalizeVolatileFields(StableJsonWriter.ToStableJson(value));
        var goldenPath = Path.Combine(GoldenDir, goldenFileName);

        if (Environment.GetEnvironmentVariable("SSISX_REGENERATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenDir);
            File.WriteAllText(goldenPath, actual);
            return;
        }

        Assert.True(File.Exists(goldenPath),
            $"Golden file missing: {goldenPath}. Regenerate with 'SSISX_REGENERATE_GOLDEN=1 dotnet test'.");

        var expected = NormalizeVolatileFields(File.ReadAllText(goldenPath));
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Fields whose value legitimately varies by machine/checkout -- absolute source
    /// paths (depend on where the repo happens to be cloned) and file mtime (git does
    /// not preserve mtimes across clones) -- rather than by anything the extractor got
    /// right or wrong. Applied identically to both sides, so it's a no-op for genuine
    /// content differences.
    ///
    /// Matched by name pattern ("Source...Path") rather than an explicit list: an
    /// earlier version of this matched a bare "FilePath" property name and silently
    /// wiped <c>ParsedConnectionString.FilePath</c> too (a flat-file connection
    /// manager's actual extracted CSV path -- real content a golden test exists to
    /// protect, not environment noise) because PackageSpec used to have its own
    /// same-named "FilePath" field for the .dtsx's own location. Renamed to
    /// SourceDtsxPath specifically so this class of ambiguity can't recur silently --
    /// don't reintroduce a bare "FilePath" property on any spec type.
    /// </summary>
    private static string NormalizeVolatileFields(string json)
    {
        json = Regex.Replace(json, @"""(Source\w*Path)"": "".*?""", @"""$1"": ""<normalized>""");
        json = Regex.Replace(json, @"""LastWriteTimeUtc"": "".*?""", @"""LastWriteTimeUtc"": ""<normalized>""");
        return json;
    }
}
