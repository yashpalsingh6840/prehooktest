using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

public class SqlRowReaderEmitterTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Emit_ReadsEveryColumnByName_NotByOrdinal()
    {
        var package = LoadSyntheticFixture("SyntheticOleDbSourceTransform.dtsx");
        var source = TestFixtures.FindComponent(package, "Microsoft.OLEDBSource", "OLE DB Source");

        var result = SqlRowReaderEmitter.Emit("Generated.Sql", "OrderSqlRow", source);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Sql/OrderSqlRowReader.cs", file.RelativePath);

        Assert.Contains("public static class OrderSqlRowReader", file.Content);
        Assert.Contains("public static OrderSqlRow Read(DbDataReader reader) => new()", file.Content);
        Assert.Contains("ID = reader.GetFieldValue<int>(reader.GetOrdinal(\"ID\")),", file.Content);
        Assert.Contains("Amount = reader.GetFieldValue<decimal>(reader.GetOrdinal(\"Amount\")),", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
