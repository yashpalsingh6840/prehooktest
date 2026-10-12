using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

/// <summary><c>ssisx generate --storage local|s3</c>: the same package, generated against the local
/// filesystem (the default) or an S3 bucket. One mode per package, never mixed.</summary>
public class StorageModeTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    private static JsonNode Json(PackageGenerateResult result, string path) =>
        JsonNode.Parse(Assert.Single(result.Files, f => f.RelativePath == path).Content)!;

    [Fact]
    public void Local_IsTheDefault_AndRegistersNothingExtra()
    {
        var result = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticExcelSource.dtsx"), namespacePrefix: null);

        Assert.DoesNotContain("AddS3FileStore", Assert.Single(result.Files, f => f.RelativePath == "Program.cs").Content);
        Assert.Null(Json(result, "appsettings.json")["Storage"]);
        Assert.DoesNotContain(result.Gaps, g => g.Location.EndsWith(".Storage"));
        Assert.Equal("TestData", (string?)Json(result, "appsettings.Development.json")["FileSource"]!["Files"]!["Excel Source"]!["SourceFolder"]);
    }

    [Fact]
    public void S3_RegistersTheS3Store_AndEmitsAStorageSectionWithAnEmptyBucket()
    {
        var result = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticExcelSource.dtsx"), namespacePrefix: null, storage: StorageMode.S3);

        Assert.Contains("builder.Services.AddS3FileStore(builder.Configuration);",
            Assert.Single(result.Files, f => f.RelativePath == "Program.cs").Content);

        var storage = Json(result, "appsettings.json")["Storage"]!;
        Assert.Equal("", (string?)storage["Bucket"]); // nothing in a .dtsx names a bucket -- startup fails fast until it is set
        Assert.Equal("", (string?)storage["Region"]);
    }

    [Fact]
    public void S3_TurnsEveryLocalSourceFolderIntoAKeyPrefix_AndFlagsItAsANonBlockingGap()
    {
        var local = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticExcelSource.dtsx"), namespacePrefix: null);
        var s3 = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticExcelSource.dtsx"), namespacePrefix: null, storage: StorageMode.S3);

        var localFolder = (string)Json(local, "appsettings.json")["FileSource"]!["Files"]!["Excel Source"]!["SourceFolder"]!;
        var prefix = (string)Json(s3, "appsettings.json")["FileSource"]!["Files"]!["Excel Source"]!["SourceFolder"]!;
        Assert.Equal(PackageGenerator.ToS3KeyPrefix(localFolder), prefix);
        Assert.DoesNotContain('\\', prefix);
        Assert.DoesNotContain(':', prefix);

        // Same file name either way -- only the folder changes.
        Assert.Equal(
            (string?)Json(local, "appsettings.json")["FileSource"]!["Files"]!["Excel Source"]!["SourceFileName"],
            (string?)Json(s3, "appsettings.json")["FileSource"]!["Files"]!["Excel Source"]!["SourceFileName"]);

        var gap = Assert.Single(s3.Gaps, g => g.Location.EndsWith(".Storage"));
        Assert.False(gap.IsBlocking);
    }

    [Fact]
    public void S3_NoLongerPointsADevelopmentRunAtALocalTestDataFolder()
    {
        var result = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticExcelSource.dtsx"), namespacePrefix: null, storage: StorageMode.S3);

        Assert.Empty(Json(result, "appsettings.Development.json")["FileSource"]!["Files"]!.AsObject());
    }

    [Fact]
    public void BothModes_PassTheStoreToEveryFileTouchingConstructor()
    {
        foreach (var mode in new[] { StorageMode.Local, StorageMode.S3 })
        {
            var result = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticExcelSource.dtsx"), namespacePrefix: null, storage: mode);
            var cls = Assert.Single(result.Files, f => f.RelativePath == "SyntheticExcelSource.cs").Content;
            Assert.Contains("using Etl.Core.Storage;", cls);
            Assert.Contains("internal IFileStore Files() => services.GetRequiredService<IFileStore>();", cls);
            Assert.Contains("Reader.Read, Files());", cls);
        }
    }

    [Fact]
    public void APureSqlPackage_GetsNoFilesHelperInEitherMode()
    {
        // No file source, sink or step anywhere -- the storage types should not leak in.
        var result = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticOleDbSourceTransform.dtsx"), namespacePrefix: null, storage: StorageMode.S3);
        var cls = Assert.Single(result.Files, f => f.RelativePath == "SyntheticOleDbSourceTransform.cs").Content;
        Assert.DoesNotContain("IFileStore", cls);
    }

    private static string TestFile(PackageGenerateResult r, string name) =>
        Assert.Single(r.SiblingFiles, f => f.RelativePath.EndsWith(name)).Content;

    [Fact]
    public void Local_TestHarness_UsesTheRealFilesystem_AndEmitsNoFakeStore()
    {
        var result = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticExcelSource.dtsx"), namespacePrefix: null);

        Assert.DoesNotContain(result.SiblingFiles, f => f.RelativePath.EndsWith("TestDoubles/FakeFileStore.cs"));
        var harness = TestFile(result, "TestDoubles/PackageHarness.cs");
        Assert.Contains("Files = LocalFileStore.Instance;", harness);
        Assert.DoesNotContain("useRealS3", harness);
        Assert.DoesNotContain("FakeFileStore", harness);
    }

    [Fact]
    public void S3_TestHarness_DefaultsToAnInMemoryStore_AndOnlyHitsRealS3WhenAskedTo()
    {
        var result = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticExcelSource.dtsx"), namespacePrefix: null, storage: StorageMode.S3);

        var harness = TestFile(result, "TestDoubles/PackageHarness.cs");
        Assert.Contains("public PackageHarness(bool useSampleDataForFileSources = false, bool useRealS3 = false)", harness);
        Assert.Contains("Files = useRealS3 ? RealS3Store() : new FakeFileStore();", harness);
        Assert.Contains("SSISX_TEST_S3_BUCKET", harness); // a real read refuses to run without a bucket
        Assert.Contains("S3 semantics", TestFile(result, "TestDoubles/FakeFileStore.cs"));
    }

    [Fact]
    public void S3_OnlyTheIntegrationReadUsesRealS3_TheNameTestStaysInMemory()
    {
        var result = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticExcelSource.dtsx"), namespacePrefix: null, storage: StorageMode.S3);
        var test = TestFile(result, "ExcelSourceSourceTests.cs");

        Assert.Equal(1, test.Split("useRealS3: true").Length - 1);
        Assert.Contains("[Trait(\"Category\", \"Integration\")]", test);
        Assert.Contains("using var harness = new PackageHarness();", test); // the .Name fact
    }

    [Fact]
    public void BothModes_GeneratedFileTests_GoThroughTheHarnessNotSystemIo()
    {
        foreach (var mode in new[] { StorageMode.Local, StorageMode.S3 })
        {
            var fst = PackageGenerator.Generate(LoadSyntheticFixture("SyntheticFileSystemTask.dtsx"), namespacePrefix: null, storage: mode);
            var text = string.Join("\n", fst.SiblingFiles.Where(f => f.RelativePath.EndsWith("Tests.cs")).Select(f => f.Content));
            Assert.Contains("harness.WriteFileAsync(", text);
            Assert.Contains("harness.FileExistsAsync(", text);
            Assert.DoesNotContain("File.WriteAllTextAsync(harness.", text);
            Assert.DoesNotContain("File.Exists(destinationPath)", text);
        }
    }

    [Theory]
    [InlineData(@"D:\data\in", "data/in")]
    [InlineData(@"C:\", "")]
    [InlineData(@"\\server\share\in\sub", "share/in/sub")]
    [InlineData("/var/data/in/", "var/data/in")]
    [InlineData("relative/dir", "relative/dir")]
    [InlineData("TODO", "TODO")]
    public void ToS3KeyPrefix_DropsTheDriveOrShare_AndUsesForwardSlashes(string local, string expected) =>
        Assert.Equal(expected, PackageGenerator.ToS3KeyPrefix(local));
}
