namespace Ssis.Extract.Codegen.Tests;

public class CsvRowEmitterTests
{
    [Fact]
    public void Emit_ProducesEveryColumn_FromTheRealEmployeesCsvConnectionManager()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var cm = TestFixtures.FindConnectionManager(package, "CM_EmployeesCsv");

        var result = CsvRowEmitter.Emit("LoadEmployees.Csv", "EmployeeCsvRow", cm);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Equal("Csv/EmployeeCsvRow.cs", file.RelativePath);

        Assert.Contains("using Etl.Core.Ssis;", file.Content);
        Assert.Contains("public int EmployeeID { get; set; }", file.Content);
        Assert.Contains("[SsisWidth(50)]", file.Content);
        Assert.Contains("public string FirstName { get; set; } = \"\";", file.Content);
        Assert.Contains("public DateOnly HireDate { get; set; }", file.Content);
        Assert.Contains("public decimal Salary { get; set; }", file.Content);
        // State's MaximumWidth is 2 in the real .dtsx, distinct from FirstName/LastName/City/Department's 50.
        Assert.Contains("[SsisWidth(2)]", file.Content);
        Assert.Contains("public string State { get; set; } = \"\";", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ReportsAGap_WhenTheConnectionManagerHasNoFlatFileFormat()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var cm = TestFixtures.FindConnectionManager(package, "CM_TargetDb"); // an OLEDB connection manager, no FlatFileFormat

        var result = CsvRowEmitter.Emit("LoadEmployees.Csv", "TargetDbRow", cm);

        Assert.Empty(result.Files);
        var gap = Assert.Single(result.Gaps);
        Assert.Contains("CM_TargetDb", gap.Reason);
    }
}
