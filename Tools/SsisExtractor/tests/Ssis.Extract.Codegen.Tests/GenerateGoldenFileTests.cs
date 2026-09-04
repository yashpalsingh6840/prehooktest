namespace Ssis.Extract.Codegen.Tests;

/// <summary>
/// Phase 4 layer 1 of the `ssisx generate` plan: <see cref="PackageGenerator.Generate"/>'s
/// FULL output for this PoC's own two real packages, byte-matched against a committed golden
/// copy of every generated file -- not just the per-emitter unit assertions the earlier
/// phases already have. Those prove each emitter is individually correct; this proves the
/// whole orchestration in <c>PackageGenerator</c> (name derivation, which emitter gets
/// called with what, gate ordering) doesn't silently drift as the generator evolves.
///
/// Goldens are named `&lt;RelativePath&gt;.txt` (e.g. "Model/Employee.cs.txt") rather than
/// bare `.cs` so the SDK's default `**/*.cs` glob never tries to compile them as part of
/// this test project -- same reasoning the generate plan's own Phase 4 §1 calls out.
///
/// To regenerate after an intentional generator change:
///   SSISX_REGENERATE_GOLDEN=1 dotnet test tests/Ssis.Extract.Codegen.Tests
/// then review the resulting git diff under Golden/Generated/ like any other code change --
/// same discipline (and the same "did the fixture change or did the generator?" caveat) as
/// Ssis.Extract.Tests/GoldenFileTests.cs.
/// </summary>
public class GenerateGoldenFileTests
{
    [Theory]
    [InlineData("LoadEmployees.dtsx")]
    [InlineData("LoadReferenceData.dtsx")]
    public void Generate_MatchesGoldenFiles(string dtsxFileName)
    {
        var package = TestFixtures.LoadPackage(dtsxFileName);
        var result = PackageGenerator.Generate(package, namespacePrefix: null);

        Assert.NotEmpty(result.Files);
        // SiblingFiles (a {PackageName}.Tests/ starter test project, TestProjectEmitter/
        // TransformTestEmitter) are covered here too -- their own RelativePath already starts
        // with "{PackageName}.Tests/", a distinct top-level segment from result.Files' own
        // ("Model/", "Csv/", "Program.cs", ...), so the two lists can merge with no collision.
        var allFiles = result.Files.Concat(result.SiblingFiles).ToList();
        foreach (var file in allFiles.Where(f => f.RelativePath.EndsWith(".cs", StringComparison.Ordinal)))
            CodeAssertions.AssertNoSyntaxErrors(file.Content);

        AssertMatchesGolden(package.ObjectName, allFiles);
    }

    private static void AssertMatchesGolden(string packageName, List<GeneratedFile> files)
    {
        var packageGoldenDir = Path.Combine(SourceGoldenDir(), packageName);

        if (Environment.GetEnvironmentVariable("SSISX_REGENERATE_GOLDEN") == "1")
        {
            if (Directory.Exists(packageGoldenDir)) Directory.Delete(packageGoldenDir, recursive: true);
            foreach (var file in files)
            {
                var path = Path.Combine(packageGoldenDir, file.RelativePath + ".txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content);
            }
            return;
        }

        var actualByPath = files.ToDictionary(f => f.RelativePath, f => f.Content);
        var expectedFiles = Directory.Exists(packageGoldenDir)
            ? Directory.GetFiles(packageGoldenDir, "*.txt", SearchOption.AllDirectories)
            : [];
        var expectedByPath = expectedFiles.ToDictionary(
            p => Path.GetRelativePath(packageGoldenDir, p)[..^".txt".Length].Replace('\\', '/'),
            p => File.ReadAllText(p));

        Assert.True(expectedByPath.Count > 0,
            $"No golden files found under {packageGoldenDir}. Regenerate with 'SSISX_REGENERATE_GOLDEN=1 dotnet test'.");

        var missingFromActual = expectedByPath.Keys.Except(actualByPath.Keys).ToList();
        var unexpectedInActual = actualByPath.Keys.Except(expectedByPath.Keys).ToList();
        Assert.True(missingFromActual.Count == 0, $"Generator no longer produces these golden files: {string.Join(", ", missingFromActual)}");
        Assert.True(unexpectedInActual.Count == 0, $"Generator now produces these UN-golden files: {string.Join(", ", unexpectedInActual)}");

        foreach (var (path, expected) in expectedByPath)
            Assert.Equal(expected, actualByPath[path]);
    }

    // Golden files are checked in under the SOURCE tree (not bin/ where the test assembly
    // runs from) so SSISX_REGENERATE_GOLDEN=1 writes somewhere `git diff` can see.
    private static string SourceGoldenDir([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "Golden", "Generated");
}
