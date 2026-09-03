using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

public class ExcelRowEmitterTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Emit_ProducesEveryColumn_FromTheRealExcelWorksheet()
    {
        // SyntheticExcelSource.dtsx: Microsoft.ExcelSource reading the REAL, checked-in
        // synthetic-excel-source.xlsx (a copy of RBC_Demo_ETL's own sample-data/
        // DripEligibility.xlsx) -> OLE DB Destination, no transform. Every column comes back
        // r8/wstr -- Excel/ACE OLEDB has no narrower numeric type at all, confirmed real from
        // the worksheet's own actual cell content, not assumed.
        var package = LoadSyntheticFixture("SyntheticExcelSource.dtsx");
        var source = TestFixtures.FindComponent(package, "Microsoft.ExcelSource", "Excel Source");

        var result = ExcelRowEmitter.Emit("Generated.Excel", "DripRow", source);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Excel/DripRow.cs", file.RelativePath);

        Assert.Contains("namespace Generated.Excel;", file.Content);
        Assert.Contains("public sealed class DripRow", file.Content);
        Assert.Contains("public double CustomerID { get; set; }", file.Content);
        Assert.Contains("public string DripEligible { get; set; } = \"\";", file.Content);
        Assert.Contains("public string ReviewedBy { get; set; } = \"\";", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_EndsInASingleTrailingNewline_NoCarriageReturnsAnywhere()
    {
        var package = LoadSyntheticFixture("SyntheticExcelSource.dtsx");
        var source = TestFixtures.FindComponent(package, "Microsoft.ExcelSource", "Excel Source");

        var file = Assert.Single(ExcelRowEmitter.Emit("Generated.Excel", "DripRow", source).Files);

        Assert.DoesNotContain('\r', file.Content);
        Assert.EndsWith("\n", file.Content);
        Assert.False(file.Content.EndsWith("\n\n", StringComparison.Ordinal));
    }
}
