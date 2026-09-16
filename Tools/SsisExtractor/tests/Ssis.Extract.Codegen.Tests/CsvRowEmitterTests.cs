using Ssis.Extract.Model.Pipeline;

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

    /// <summary>Regression for a real batch-run bug (2026-09-16): a Flat File Source component
    /// can override a column's real pipeline output type independently of the connection
    /// manager's own declared type (its Advanced Editor's own "Conversion" behavior) -- e.g. a
    /// CM declaring DT_STR while the source's own output column for that name is DT_I4, with no
    /// Data Conversion/Derived Column anywhere downstream. Before this was recognized, the
    /// generated CSV row property was typed purely from the CM's declared (pre-conversion) type,
    /// silently disagreeing with the destination/transform -- a real CS0029 build failure.</summary>
    [Fact]
    public void Emit_PrefersTheSourceComponentsOwnOutputColumnType_WhenItOverridesTheConnectionManagersDeclaredType()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var cm = TestFixtures.FindConnectionManager(package, "CM_EmployeesCsv"); // declares FirstName as DT_STR

        var sourceComponent = new PipelineComponentSpec
        {
            RefId = "Package\\DFT\\SRC",
            Name = "SRC",
            ComponentClassId = "Microsoft.FlatFileSource",
            Outputs =
            [
                new PipelineOutputSpec
                {
                    RefId = "Package\\DFT\\SRC.Outputs[Flat File Source Output]",
                    Name = "Flat File Source Output",
                    Columns =
                    [
                        new PipelineOutputColumnSpec
                        {
                            RefId = "Package\\DFT\\SRC.Outputs[Flat File Source Output].Columns[FirstName]",
                            Name = "FirstName",
                            LineageId = "1",
                            DataType = "DT_I4", // overrides the CM's own declared DT_STR
                        },
                    ],
                },
            ],
        };

        var result = CsvRowEmitter.Emit("LoadEmployees.Csv", "EmployeeCsvRow", cm, sourceComponent);

        Assert.Empty(result.Gaps);
        var file = Assert.Single(result.Files);
        Assert.Contains("public int FirstName { get; set; }", file.Content);
        Assert.DoesNotContain("public string FirstName", file.Content);
        // A column the source component doesn't mention at all keeps the CM's own declared type.
        Assert.Contains("public DateOnly HireDate { get; set; }", file.Content);

        CodeAssertions.AssertNoSyntaxErrors(file.Content);
    }

    [Fact]
    public void Emit_ReportsAGap_WhenTheSourceComponentsOverrideTypeIsUnmapped()
    {
        var package = TestFixtures.LoadPackage("LoadEmployees.dtsx");
        var cm = TestFixtures.FindConnectionManager(package, "CM_EmployeesCsv");

        var sourceComponent = new PipelineComponentSpec
        {
            RefId = "Package\\DFT\\SRC",
            Name = "SRC",
            ComponentClassId = "Microsoft.FlatFileSource",
            Outputs =
            [
                new PipelineOutputSpec
                {
                    RefId = "Package\\DFT\\SRC.Outputs[Flat File Source Output]",
                    Name = "Flat File Source Output",
                    Columns =
                    [
                        new PipelineOutputColumnSpec
                        {
                            RefId = "Package\\DFT\\SRC.Outputs[Flat File Source Output].Columns[FirstName]",
                            Name = "FirstName",
                            LineageId = "1",
                            DataType = "DT_NOT_A_REAL_TYPE",
                        },
                    ],
                },
            ],
        };

        var result = CsvRowEmitter.Emit("LoadEmployees.Csv", "EmployeeCsvRow", cm, sourceComponent);

        var gap = Assert.Single(result.Gaps, g => g.Location.EndsWith(".FirstName"));
        Assert.Contains("overrides", gap.Reason);
        Assert.Contains("DT_NOT_A_REAL_TYPE", gap.Reason);
    }
}
