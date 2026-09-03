using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

public class SqlRowEmitterTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Emit_ProducesEveryColumn_FromTheRealOleDbSourceOutput()
    {
        var package = LoadSyntheticFixture("SyntheticOleDbSourceTransform.dtsx");
        var source = TestFixtures.FindComponent(package, "Microsoft.OLEDBSource", "OLE DB Source");

        var result = SqlRowEmitter.Emit("Generated.Sql", "OrderSqlRow", source);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Sql/OrderSqlRow.cs", file.RelativePath);

        Assert.Contains("namespace Generated.Sql;", file.Content);
        Assert.Contains("public sealed class OrderSqlRow", file.Content);
        Assert.Contains("public int ID { get; set; }", file.Content);
        Assert.Contains("public decimal Amount { get; set; }", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_EndsInASingleTrailingNewline_NoCarriageReturnsAnywhere()
    {
        var package = LoadSyntheticFixture("SyntheticOleDbSourceTransform.dtsx");
        var source = TestFixtures.FindComponent(package, "Microsoft.OLEDBSource", "OLE DB Source");

        var file = Assert.Single(SqlRowEmitter.Emit("Generated.Sql", "OrderSqlRow", source).Files);

        Assert.DoesNotContain('\r', file.Content);
        Assert.EndsWith("\n", file.Content);
        Assert.False(file.Content.EndsWith("\n\n", StringComparison.Ordinal));
    }
}
