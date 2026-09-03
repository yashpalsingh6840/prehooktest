using System.Runtime.CompilerServices;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Codegen.Tests;

public class ExcelRowReaderEmitterTests
{
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static PackageSpec LoadSyntheticFixture(string dtsxFileName)
    {
        var fixturesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ThisFilePath())!, "..", "Ssis.Extract.Tests", "Fixtures"));
        return DtsxPackageReader.Read(Path.Combine(fixturesDir, dtsxFileName), noRedact: false);
    }

    [Fact]
    public void Emit_ReadsEveryColumnByOrdinalPosition_NotByName()
    {
        // Confirmed real (Etl.Core.Excel.ExcelRowSource's own doc comment): the raw
        // IExcelDataReader (no .AsDataSet() header binding) throws NotSupportedException from
        // GetName(i) and has no working GetOrdinal(name) either -- it is a plain positional
        // grid. Ordinals here are 0/1/2, matching the worksheet's own real left-to-right column
        // order (CustomerID, DripEligible, ReviewedBy).
        var package = LoadSyntheticFixture("SyntheticExcelSource.dtsx");
        var source = TestFixtures.FindComponent(package, "Microsoft.ExcelSource", "Excel Source");

        var result = ExcelRowReaderEmitter.Emit("Generated.Excel", "DripRow", source);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Excel/DripRowReader.cs", file.RelativePath);

        Assert.Contains("using ExcelDataReader;", file.Content);
        Assert.Contains("public static class DripRowReader", file.Content);
        Assert.Contains("public static DripRow Read(IExcelDataReader reader) => new()", file.Content);
        Assert.Contains("CustomerID = reader.GetDouble(0),", file.Content);
        Assert.Contains("DripEligible = reader.GetString(1),", file.Content);
        Assert.Contains("ReviewedBy = reader.GetString(2),", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }
}
